using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Cloud;

/// <summary>
/// Deploy-time work for a RavenDB Cloud product, one method per pipeline step: the product itself
/// (<see cref="RavenDBCloudProducts"/>), its databases, and the applications' client certificates
/// (<see cref="RavenDBCloudCertificateReconciler"/>). Independent of the pipeline plumbing so it can be tested
/// directly.
/// </summary>
/// <remarks>
/// The deployment state only records which product this deployment created. Everything else is read back from the
/// product, so a deployment from a machine without that state (a CI runner) finds the same product and ends with the
/// same certificates.
/// </remarks>
internal sealed class RavenDBCloudProvisioner(
    RavenDBCloudApiClient client,
    IDeploymentStateManager state,
    IRavenDBServerAdministrationFactory servers,
    ILogger logger)
{
    private readonly RavenDBCloudProducts _products = new(client, state, logger);
    private readonly RavenDBCloudCertificateReconciler _certificates = new(logger);

    public Task ProvisionAsync(RavenDBCloudDeployment deployment, CancellationToken cancellationToken) =>
        _products.ProvisionAsync(deployment, cancellationToken);

    public async Task EnsureDatabasesAsync(RavenDBCloudDeployment deployment, CancellationToken cancellationToken)
    {
        var databases = deployment.Server.DatabasesToCreate;

        if (databases.Count == 0)
        {
            return;
        }

        using var server = await ConnectAsync(deployment, cancellationToken).ConfigureAwait(false);

        foreach (var database in databases)
        {
            if (await server.DatabaseExistsAsync(database, cancellationToken).ConfigureAwait(false) ||
                !await server.CreateDatabaseAsync(database, deployment.NodeCount, cancellationToken).ConfigureAwait(false))
            {
                logger.LogInformation("Database '{Database}' already exists.", database);
                continue;
            }

            logger.LogInformation("Database '{Database}' created (replication factor {Factor}).", database, deployment.NodeCount);
        }
    }

    /// <inheritdoc cref="RavenDBCloudCertificateReconciler.ReconcileAsync"/>
    public async Task EnsureClientCertificatesAsync(RavenDBCloudDeployment deployment, ClientCertificatePlan plan, CancellationToken cancellationToken)
    {
        if (!IsSecured(deployment))
        {
            if (plan.Requests.Count > 0)
            {
                throw new InvalidOperationException(
                    $"RavenDB Cloud product '{deployment.ProductName}' is not served over HTTPS ({deployment.Endpoint.Url}); " +
                    "client certificates need a secured server.");
            }

            return;
        }

        using var server = await ConnectAsync(deployment, cancellationToken).ConfigureAwait(false);
        await _certificates.ReconcileAsync(server, deployment, plan, cancellationToken).ConfigureAwait(false);
    }

    public async Task DestroyAsync(RavenDBCloudDeployment deployment, ClientCertificatePlan certificates, CancellationToken cancellationToken)
    {
        var leftRunning = await _products.DestroyAsync(deployment, cancellationToken).ConfigureAwait(false);

        foreach (var request in certificates.Requests)
        {
            RavenDBCloudCertificateReconciler.DeleteFiles(deployment, certificates, request.Consumer);
        }

        // A terminated product takes its certificates with it.
        if (leftRunning)
        {
            await RevokeClientCertificatesAsync(deployment, certificates, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RevokeClientCertificatesAsync(RavenDBCloudDeployment deployment, ClientCertificatePlan certificates, CancellationToken cancellationToken)
    {
        if (!IsSecured(deployment))
        {
            return;
        }

        try
        {
            using var server = await ConnectAsync(deployment, cancellationToken).ConfigureAwait(false);
            await _certificates.RevokeAllAsync(server, certificates, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Could not connect to RavenDB Cloud product '{Product}' to revoke the client certificates named '{Prefix}*'. " +
                "Remove them in the RavenDB Studio.",
                deployment.ProductName,
                certificates.NamePrefix);
        }
    }

    private async Task<IRavenDBServerAdministration> ConnectAsync(RavenDBCloudDeployment deployment, CancellationToken cancellationToken)
    {
        var url = deployment.Endpoint.Url
            ?? throw new InvalidOperationException($"RavenDB Cloud product '{deployment.ProductName}' has not been resolved yet.");

        var certificate = IsSecured(deployment)
            ? await _products.GetAdminCertificateAsync(deployment, cancellationToken).ConfigureAwait(false)
            : null;

        return servers.Create(url, certificate);
    }

    private static bool IsSecured(RavenDBCloudDeployment deployment) =>
        deployment.Endpoint.Url?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true;
}
