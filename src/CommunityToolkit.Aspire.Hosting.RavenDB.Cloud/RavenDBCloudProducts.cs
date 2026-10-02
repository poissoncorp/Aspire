using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Cloud;

/// <summary>
/// The lifecycle of a RavenDB Cloud product through the Cloud API: find or create it, wait until it is active, and
/// terminate it on destroy when this deployment created it.
/// </summary>
/// <remarks>
/// The deployment state only records which product this deployment created. Everything else is read back from the
/// product, so a deployment from a machine without that state (a CI runner) finds the same product.
/// </remarks>
internal sealed class RavenDBCloudProducts(RavenDBCloudApiClient client, IDeploymentStateManager state, ILogger logger)
{
    private const string ProductIdKey = "productId";
    private const string CreatedByDeploymentKey = "createdByDeployment";

    public async Task ProvisionAsync(RavenDBCloudDeployment deployment, CancellationToken cancellationToken)
    {
        var section = await state.AcquireSectionAsync(deployment.StateSectionName, cancellationToken).ConfigureAwait(false);
        var recordedId = ReadString(section, ProductIdKey);
        var (productId, details) = await FindAsync(deployment, recordedId, cancellationToken).ConfigureAwait(false);

        // Found by name, not recorded by this deployment: someone else's until proven otherwise.
        var createdByDeployment = productId is not null && productId == recordedId && ReadBool(section, CreatedByDeploymentKey);

        if (productId is null)
        {
            var request = await BuildCreateRequestAsync(deployment, cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "Creating RavenDB Cloud product '{Product}' ({Tier}, {Provider} {Region}, {Instance}, {Disk} GB, subdomain {Subdomain}).",
                request.DisplayName,
                request.Tier,
                request.CloudProvider,
                request.Region,
                request.InstanceTypeName,
                request.DiskSize,
                request.SubdomainName);

            productId = await client.CreateProductAsync(request, cancellationToken).ConfigureAwait(false);
            createdByDeployment = true;

            // Recorded right away: a deployment interrupted while the product is being created must find it again,
            // and destroy must know that it may terminate it.
            section = await RecordAsync(deployment, section, productId, createdByDeployment, cancellationToken).ConfigureAwait(false);
            details = null;
        }
        else
        {
            logger.LogInformation("Using RavenDB Cloud product '{Product}' ({ProductId}).", deployment.ProductName, productId);
        }

        details = await WaitForActiveAsync(deployment, productId, details, cancellationToken).ConfigureAwait(false);
        Resolve(deployment, productId, details);

        await RecordAsync(deployment, section, productId, createdByDeployment, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("RavenDB Cloud product '{Product}' is active at {Url}.", deployment.ProductName, deployment.Endpoint.Url);
    }

    /// <summary>
    /// Terminates the product when this deployment created it and <see cref="RavenDBCloudOptions.TerminateOnDestroy"/>
    /// is set; any other product is left running.
    /// </summary>
    /// <returns>Whether a product is left running. It is then resolved on <paramref name="deployment"/>.</returns>
    public async Task<bool> DestroyAsync(RavenDBCloudDeployment deployment, CancellationToken cancellationToken)
    {
        var section = await state.AcquireSectionAsync(deployment.StateSectionName, cancellationToken).ConfigureAwait(false);
        var recordedId = ReadString(section, ProductIdKey);
        var (productId, details) = await FindAsync(deployment, recordedId, cancellationToken).ConfigureAwait(false);

        if (productId is null || details is null)
        {
            logger.LogInformation("RavenDB Cloud product '{Product}' does not exist; nothing to destroy.", deployment.ProductName);
            await state.DeleteSectionAsync(section, cancellationToken).ConfigureAwait(false);
            return false;
        }

        Resolve(deployment, productId, details);
        var createdByDeployment = productId == recordedId && ReadBool(section, CreatedByDeploymentKey);

        if (createdByDeployment && deployment.Options.TerminateOnDestroy)
        {
            logger.LogInformation("Terminating RavenDB Cloud product '{Product}' ({ProductId}).", deployment.ProductName, productId);

            await client.TerminateProductAsync(productId, cancellationToken).ConfigureAwait(false);
            await state.DeleteSectionAsync(section, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (createdByDeployment)
        {
            // Aspire clears the deployment state after destroy, so a later deployment finds this product by name and
            // treats it as someone else's: from here on only the portal terminates it.
            logger.LogWarning(
                "RavenDB Cloud product '{Product}' ({ProductId}) is left running because TerminateOnDestroy is not set; " +
                "terminating deletes its data. Terminate it in the portal once it is no longer needed.",
                deployment.ProductName,
                productId);
        }
        else
        {
            logger.LogWarning(
                "RavenDB Cloud product '{Product}' ({ProductId}) was not created by this deployment and is left running.",
                deployment.ProductName,
                productId);
        }

        return true;
    }

    /// <summary>
    /// The product's admin certificate, from the Cloud API. Downloaded once per deployment: the API accepts about one
    /// request per second.
    /// </summary>
    public async Task<X509Certificate2> GetAdminCertificateAsync(RavenDBCloudDeployment deployment, CancellationToken cancellationToken)
    {
        if (deployment.AdminCertificate is null)
        {
            var productId = deployment.ProductId
                ?? throw new InvalidOperationException($"RavenDB Cloud product '{deployment.ProductName}' has not been resolved yet.");
            var bundle = await client.GetClientCertificateAsync(productId, cancellationToken).ConfigureAwait(false);

            deployment.AdminCertificate = RavenDBCloudClientCertificates.ExtractPfx(bundle)
                ?? throw new InvalidOperationException($"The certificate bundle of RavenDB Cloud product '{deployment.ProductName}' contains no .pfx file.");
        }

        return RavenDBCloudClientCertificates.Load(deployment.AdminCertificate);
    }

    /// <summary>
    /// The product recorded in the deployment state, else the one with the configured name. Looking it up by name
    /// is what keeps a CI runner, which has no deployment state, from creating a second product.
    /// </summary>
    private async Task<(string? ProductId, ProductDetails? Details)> FindAsync(
        RavenDBCloudDeployment deployment,
        string? recordedProductId,
        CancellationToken cancellationToken)
    {
        if (recordedProductId is not null)
        {
            var recorded = await client.GetProductAsync(recordedProductId, cancellationToken).ConfigureAwait(false);

            if (recorded is not null && !IsGone(recorded.Status))
            {
                return (recordedProductId, recorded);
            }

            logger.LogWarning("RavenDB Cloud product {ProductId} recorded by an earlier deployment is gone.", recordedProductId);
        }

        var products = await client.ListProductsAsync(cancellationToken).ConfigureAwait(false);
        var live = new List<(string Id, ProductDetails Details)>();

        // A terminated product stays in the list for a while: only the live ones count.
        foreach (var match in products.Where(p => p.Id is not null && string.Equals(p.Name, deployment.ProductName, StringComparison.OrdinalIgnoreCase)))
        {
            if (await client.GetProductAsync(match.Id!, cancellationToken).ConfigureAwait(false) is { } details && !IsGone(details.Status))
            {
                live.Add((match.Id!, details));
            }
        }

        if (live.Count > 1)
        {
            throw new InvalidOperationException(
                $"The account has {live.Count} RavenDB Cloud products named '{deployment.ProductName}'. Give the product " +
                "a unique name with ProductName.");
        }

        return live.Count == 0 ? (null, null) : live[0];
    }

    private async Task<ProductCreateRequest> BuildCreateRequestAsync(RavenDBCloudDeployment deployment, CancellationToken cancellationToken)
    {
        var options = deployment.Options;

        if (options.AllowedIps.Count == 0)
        {
            throw new InvalidOperationException(
                $"Creating RavenDB Cloud product '{deployment.ProductName}' needs at least one allowed IP range; the Cloud " +
                "API offers no way to add them later. Set AllowedIps, for example options.WithAllowedIps(\"203.0.113.0/24\").");
        }

        var provider = options.Provider.ToString();
        var instanceType = options.InstanceType;
        var diskSize = options.DiskSizeInGb;

        if (instanceType is null || diskSize is null)
        {
            var types = await client.GetInstanceTypesAsync(provider, deployment.Region, cancellationToken).ConfigureAwait(false);

            var chosen = instanceType is not null
                ? types.FirstOrDefault(t => string.Equals(t.Name, instanceType, StringComparison.OrdinalIgnoreCase))
                : types
                    .Where(t => string.Equals(t.Tier, options.Tier.ToString(), StringComparison.OrdinalIgnoreCase) && t.Name is not null)
                    .OrderBy(t => t.Parameters?.VirtualCpus ?? int.MaxValue)
                    .ThenBy(t => t.Parameters?.Ram ?? double.MaxValue)
                    .FirstOrDefault();

            instanceType ??= chosen?.Name
                ?? throw new InvalidOperationException(
                    $"RavenDB Cloud offers no {options.Tier} instance type in {provider} {deployment.Region}. Set InstanceType or pick another region.");

            diskSize ??= chosen?.Parameters?.AvailableDiskSizes?.Where(s => s > 0).DefaultIfEmpty().Min() is int smallest and > 0
                ? smallest
                : throw new InvalidOperationException($"No disk size is listed for instance type '{instanceType}'. Set DiskSizeInGb.");
        }

        var releaseChannel = options.ReleaseChannel;

        if (releaseChannel is null)
        {
            var channels = await client.GetReleaseChannelsAsync(cancellationToken).ConfigureAwait(false);

            releaseChannel = channels.DefaultReleaseChannel
                ?? channels.ReleaseChannels?.FirstOrDefault()?.Name
                ?? throw new InvalidOperationException("RavenDB Cloud lists no release channel. Set ReleaseChannel.");
        }

        return new ProductCreateRequest(
            CloudProvider: provider,
            InstanceTypeName: instanceType,
            DisplayName: deployment.ProductName,
            ReleaseChannel: releaseChannel,
            SubdomainName: deployment.Subdomain,
            Tier: options.Tier.ToString(),
            Region: deployment.Region,
            DiskSize: diskSize.Value,
            StorageTypeName: options.StorageType.ToString(),
            AllowedIps: [.. options.AllowedIps]);
    }

    private async Task<ProductDetails> WaitForActiveAsync(
        RavenDBCloudDeployment deployment,
        string productId,
        ProductDetails? details,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + deployment.Options.ProvisioningTimeout;
        string? lastStatus = null;

        while (true)
        {
            details ??= await client.GetProductAsync(productId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"RavenDB Cloud product '{deployment.ProductName}' disappeared while it was being provisioned.");

            if (!string.Equals(details.Status, lastStatus, StringComparison.Ordinal))
            {
                logger.LogInformation("RavenDB Cloud product '{Product}': {Status}.", deployment.ProductName, details.Status);
                lastStatus = details.Status;
            }

            switch (details.Status)
            {
                case ProductStatus.Active:
                    return details;

                case ProductStatus.Error:
                    throw new InvalidOperationException($"RavenDB Cloud product '{deployment.ProductName}' failed to provision. See the portal for details.");

                case ProductStatus.AwaitingPayment:
                    throw new InvalidOperationException(
                        $"RavenDB Cloud product '{deployment.ProductName}' is awaiting payment. Complete it in the portal and deploy again.");

                case ProductStatus.Terminating:
                case ProductStatus.Terminated:
                    throw new InvalidOperationException($"RavenDB Cloud product '{deployment.ProductName}' is being terminated.");
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"RavenDB Cloud product '{deployment.ProductName}' was still '{details.Status}' after {deployment.Options.ProvisioningTimeout}.");
            }

            // The Cloud API accepts about one request per second per endpoint.
            await Task.Delay(deployment.PollInterval, cancellationToken).ConfigureAwait(false);
            details = null;
        }
    }

    /// <summary>Takes the product's id, size and URL from its details; the API lists host names without a scheme.</summary>
    private static void Resolve(RavenDBCloudDeployment deployment, string productId, ProductDetails details)
    {
        var url = details.Dns?.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d))
            ?? throw new InvalidOperationException($"RavenDB Cloud product '{deployment.ProductName}' is active but reports no URL.");

        deployment.ProductId = productId;
        deployment.NodeCount = Math.Max(details.NodeTags?.Count ?? 1, 1);
        deployment.Endpoint.Url = (url.Contains("://", StringComparison.Ordinal) ? url : "https://" + url).TrimEnd('/');
    }

    private async Task<DeploymentStateSection> RecordAsync(
        RavenDBCloudDeployment deployment,
        DeploymentStateSection section,
        string productId,
        bool createdByDeployment,
        CancellationToken cancellationToken)
    {
        section.Data[ProductIdKey] = JsonValue.Create(productId);
        section.Data[CreatedByDeploymentKey] = JsonValue.Create(createdByDeployment);

        await state.SaveSectionAsync(section, cancellationToken).ConfigureAwait(false);
        return await state.AcquireSectionAsync(deployment.StateSectionName, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsGone(string? status) =>
        status is ProductStatus.Terminated or ProductStatus.Terminating;

    private static string? ReadString(DeploymentStateSection section, string key) =>
        section.Data.TryGetPropertyValue(key, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool ReadBool(DeploymentStateSection section, string key) =>
        section.Data.TryGetPropertyValue(key, out var node) && node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
}
