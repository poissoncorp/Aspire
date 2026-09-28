using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Cloud;

/// <summary>
/// Deploy-time work for a RavenDB Cloud product: find or create it, wait for it, create the declared databases and
/// terminate it on destroy. Independent of the pipeline plumbing so it can be tested directly.
/// </summary>
internal sealed class RavenDBCloudProvisioner(RavenDBCloudApiClient client, IDeploymentStateManager state, ILogger logger)
{
    private const string ProductIdKey = "productId";
    private const string UrlKey = "url";
    private const string CreatedByDeploymentKey = "createdByDeployment";

    public async Task ProvisionAsync(RavenDBCloudDeployment deployment, CancellationToken cancellationToken)
    {
        var section = await state.AcquireSectionAsync(deployment.StateSectionName, cancellationToken).ConfigureAwait(false);
        var createdByDeployment = ReadBool(section, CreatedByDeploymentKey);

        var (productId, details) = await FindProductAsync(deployment, ReadString(section, ProductIdKey), cancellationToken).ConfigureAwait(false);

        if (productId is not null && !string.Equals(productId, ReadString(section, ProductIdKey), StringComparison.Ordinal))
        {
            // Found by name, not recorded by this deployment: someone else's until proven otherwise.
            createdByDeployment = false;
        }

        if (productId is null)
        {
            if (deployment.Options.IsExisting)
            {
                throw new InvalidOperationException(
                    $"No RavenDB Cloud product named '{deployment.ProductName}' exists in the account. AsExisting(...) " +
                    "never creates a product: create it in the portal, fix the name, or drop AsExisting to let the " +
                    "deployment create it.");
            }

            var request = await BuildCreateRequestAsync(deployment, cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "Creating RavenDB Cloud product '{Product}' ({Tier}, {Provider} {Region}, {Instance}, {Disk} GB).",
                request.DisplayName,
                request.Tier,
                request.CloudProvider,
                request.Region,
                request.InstanceTypeName,
                request.DiskSize);

            productId = await client.CreateProductAsync(request, cancellationToken).ConfigureAwait(false);
            createdByDeployment = true;

            // Recorded right away: a deployment interrupted while the product is being created must find it again.
            section.Data[ProductIdKey] = JsonValue.Create(productId);
            section.Data[CreatedByDeploymentKey] = JsonValue.Create(true);
            await state.SaveSectionAsync(section, cancellationToken).ConfigureAwait(false);
            section = await state.AcquireSectionAsync(deployment.StateSectionName, cancellationToken).ConfigureAwait(false);

            details = null;
        }
        else
        {
            logger.LogInformation("Using RavenDB Cloud product '{Product}' ({ProductId}).", deployment.ProductName, productId);
        }

        details = await WaitForActiveAsync(deployment, productId, details, cancellationToken).ConfigureAwait(false);

        var url = details.Dns?.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d))
            ?? throw new InvalidOperationException($"RavenDB Cloud product '{deployment.ProductName}' is active but reports no URL.");

        if (!url.Contains("://", StringComparison.Ordinal))
        {
            url = "https://" + url;
        }

        deployment.ProductId = productId;
        deployment.NodeCount = Math.Max(details.NodeTags?.Count ?? 1, 1);
        deployment.Endpoint.Url = url.TrimEnd('/');

        section.Data[ProductIdKey] = JsonValue.Create(productId);
        section.Data[UrlKey] = JsonValue.Create(deployment.Endpoint.Url);
        section.Data[CreatedByDeploymentKey] = JsonValue.Create(createdByDeployment);
        await state.SaveSectionAsync(section, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("RavenDB Cloud product '{Product}' is active at {Url}.", deployment.ProductName, deployment.Endpoint.Url);
    }

    public async Task EnsureDatabasesAsync(RavenDBCloudDeployment deployment, CancellationToken cancellationToken)
    {
        var databases = deployment.Server.DatabasesToCreate;

        // An existing product belongs to someone else: the deployment connects to it and changes nothing.
        if (databases.Count == 0 || deployment.Options.IsExisting)
        {
            return;
        }

        var url = deployment.Endpoint.Url
            ?? throw new InvalidOperationException($"RavenDB Cloud product '{deployment.ProductName}' has not been resolved yet.");

        using var handler = new HttpClientHandler();
        using var certificate = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? await DownloadClientCertificateAsync(deployment, cancellationToken).ConfigureAwait(false)
            : null;

        if (certificate is not null)
        {
            handler.ClientCertificates.Add(certificate);
        }

        using var http = new HttpClient(handler) { BaseAddress = new Uri(url + "/") };

        foreach (var database in databases)
        {
            var name = Uri.EscapeDataString(database);

            using var probe = await http.GetAsync($"databases?name={name}", cancellationToken).ConfigureAwait(false);

            if (probe.StatusCode == HttpStatusCode.OK)
            {
                logger.LogInformation("Database '{Database}' already exists.", database);
                continue;
            }

            using var body = new StringContent(JsonSerializer.Serialize(new { DatabaseName = database }), Encoding.UTF8, "application/json");
            using var create = await http.PutAsync(
                $"admin/databases?name={name}&replicationFactor={deployment.NodeCount.ToString(CultureInfo.InvariantCulture)}",
                body,
                cancellationToken).ConfigureAwait(false);

            if (!create.IsSuccessStatusCode && create.StatusCode != HttpStatusCode.Conflict)
            {
                var error = await create.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                throw new InvalidOperationException(
                    $"Creating database '{database}' on '{url}' failed: {(int)create.StatusCode} {create.ReasonPhrase}. {error}".TrimEnd());
            }

            logger.LogInformation("Database '{Database}' created (replication factor {Factor}).", database, deployment.NodeCount);
        }
    }

    public async Task DestroyAsync(RavenDBCloudDeployment deployment, CancellationToken cancellationToken)
    {
        if (deployment.Options.IsExisting)
        {
            logger.LogInformation("RavenDB Cloud product '{Product}' is an existing product and is left untouched.", deployment.ProductName);
            return;
        }

        var section = await state.AcquireSectionAsync(deployment.StateSectionName, cancellationToken).ConfigureAwait(false);
        var productId = ReadString(section, ProductIdKey);

        if (productId is null)
        {
            logger.LogInformation("No RavenDB Cloud product is recorded for '{Resource}'; nothing to terminate.", deployment.Server.Name);
            return;
        }

        if (!ReadBool(section, CreatedByDeploymentKey))
        {
            logger.LogWarning(
                "RavenDB Cloud product '{Product}' ({ProductId}) was not created by this deployment and is left running.",
                deployment.ProductName,
                productId);
            return;
        }

        if (!deployment.Options.TerminateOnDestroy)
        {
            logger.LogWarning(
                "RavenDB Cloud product '{Product}' ({ProductId}) is left running: terminating deletes its data. Set " +
                "TerminateOnDestroy to terminate it, or terminate it in the portal.",
                deployment.ProductName,
                productId);
            return;
        }

        logger.LogInformation("Terminating RavenDB Cloud product '{Product}' ({ProductId}).", deployment.ProductName, productId);

        await client.TerminateProductAsync(productId, cancellationToken).ConfigureAwait(false);
        await state.DeleteSectionAsync(section, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The product recorded in the deployment state, else the one with the configured name. Looking it up by name
    /// is what keeps a CI runner, which has no deployment state, from creating a second product.
    /// </summary>
    private async Task<(string? ProductId, ProductDetails? Details)> FindProductAsync(
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
        var matches = products
            .Where(p => p.Id is not null && string.Equals(p.Name, deployment.ProductName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"The account has {matches.Count} RavenDB Cloud products named '{deployment.ProductName}'. Give the product " +
                "a unique name with ProductName.");
        }

        if (matches.Count == 0)
        {
            return (null, null);
        }

        var details = await client.GetProductAsync(matches[0].Id!, cancellationToken).ConfigureAwait(false);

        return details is null || IsGone(details.Status) ? (null, null) : (matches[0].Id, details);
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
            var types = await client.GetInstanceTypesAsync(provider, options.Region, cancellationToken).ConfigureAwait(false);

            var chosen = instanceType is not null
                ? types.FirstOrDefault(t => string.Equals(t.Name, instanceType, StringComparison.OrdinalIgnoreCase))
                : types
                    .Where(t => string.Equals(t.Tier, options.Tier.ToString(), StringComparison.OrdinalIgnoreCase) && t.Name is not null)
                    .OrderBy(t => t.Parameters?.VirtualCpus ?? int.MaxValue)
                    .ThenBy(t => t.Parameters?.Ram ?? double.MaxValue)
                    .FirstOrDefault();

            instanceType ??= chosen?.Name
                ?? throw new InvalidOperationException(
                    $"RavenDB Cloud offers no {options.Tier} instance type in {provider} {options.Region}. Set InstanceType or pick another region.");

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
            Tier: options.Tier.ToString(),
            Region: options.Region,
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

    private async Task<X509Certificate2?> DownloadClientCertificateAsync(RavenDBCloudDeployment deployment, CancellationToken cancellationToken)
    {
        var bundle = await client.GetClientCertificateAsync(deployment.ProductId!, cancellationToken).ConfigureAwait(false);
        var pfx = ExtractPfx(bundle);

        if (pfx is null)
        {
            logger.LogWarning("The client certificate bundle of '{Product}' contains no .pfx file.", deployment.ProductName);
            return null;
        }

        try
        {
#if NET9_0_OR_GREATER
            return X509CertificateLoader.LoadPkcs12(pfx, password: null);
#else
            return new X509Certificate2(pfx, (string?)null);
#endif
        }
        catch (CryptographicException exception)
        {
            logger.LogWarning(exception, "The client certificate of '{Product}' could not be loaded.", deployment.ProductName);
            return null;
        }
    }

    /// <summary>The certificate comes as a zip bundle (pfx and pem files) or as a bare pfx.</summary>
    internal static byte[]? ExtractPfx(byte[] bundle)
    {
        if (bundle.Length < 4 || bundle[0] != 'P' || bundle[1] != 'K')
        {
            return bundle.Length == 0 ? null : bundle;
        }

        using var archive = new ZipArchive(new MemoryStream(bundle), ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return buffer.ToArray();
    }

    private static bool IsGone(string? status) =>
        status is ProductStatus.Terminated or ProductStatus.Terminating;

    private static string? ReadString(DeploymentStateSection section, string key) =>
        section.Data.TryGetPropertyValue(key, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool ReadBool(DeploymentStateSection section, string key) =>
        section.Data.TryGetPropertyValue(key, out var node) && node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
}
