using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;
using Raven.Client.ServerWide.Operations.Certificates;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Cloud;

/// <summary>
/// Deploy-time work for a RavenDB Cloud product: find or create it, wait for it, create the declared databases, issue
/// the applications' client certificates, and revoke or terminate on destroy. Independent of the pipeline plumbing so
/// it can be tested directly.
/// </summary>
internal sealed class RavenDBCloudProvisioner(
    RavenDBCloudApiClient client,
    IDeploymentStateManager state,
    IRavenDBServerAdministrationFactory servers,
    ILogger logger)
{
    private const string ProductIdKey = "productId";
    private const string UrlKey = "url";
    private const string CreatedByDeploymentKey = "createdByDeployment";
    private const string ClientCertificatesKey = "clientCertificates";
    private const string ThumbprintKey = "thumbprint";
    private const string FileKey = "file";

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

        // An existing product belongs to someone else: the deployment creates nothing in it.
        if (databases.Count == 0 || deployment.Options.IsExisting)
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

    /// <summary>
    /// Gives every application its own client certificate with access to its databases only, and writes it where
    /// the deployment artifacts expect it. A certificate issued by an earlier deployment is kept while its file is
    /// still in place, so redeploying does not hand the applications new credentials. Certificates of applications
    /// that are no longer deployed are revoked.
    /// </summary>
    public async Task EnsureClientCertificatesAsync(
        RavenDBCloudDeployment deployment,
        IReadOnlyList<ClientCertificateRequest> requests,
        CancellationToken cancellationToken)
    {
        var section = await state.AcquireSectionAsync(deployment.StateSectionName, cancellationToken).ConfigureAwait(false);
        var recorded = ReadClientCertificates(section);
        var stale = recorded.Keys.Where(c => requests.All(r => !string.Equals(r.Consumer, c, StringComparison.Ordinal))).ToList();

        if (requests.Count == 0 && stale.Count == 0)
        {
            return;
        }

        if (requests.Count > 0 && deployment.Endpoint.Url?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) != true)
        {
            throw new InvalidOperationException(
                $"RavenDB Cloud product '{deployment.ProductName}' is not served over HTTPS ({deployment.Endpoint.Url}); " +
                "client certificates need a secured server.");
        }

        using var server = await ConnectAsync(deployment, cancellationToken).ConfigureAwait(false);

        foreach (var request in requests)
        {
            var permissions = request.Databases.ToDictionary(d => d, _ => DatabaseAccess.ReadWrite, StringComparer.OrdinalIgnoreCase);
            recorded.TryGetValue(request.Consumer, out var previous);

            if (previous is not null && await TryReuseAsync(server, previous, request, permissions, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var bundle = await server.CreateClientCertificateAsync(request.CertificateName, permissions, cancellationToken).ConfigureAwait(false);
            var pfx = ExtractPfx(bundle)
                ?? throw new InvalidOperationException($"The certificate RavenDB issued for '{request.Consumer}' contains no .pfx file.");
            var thumbprint = GetThumbprint(pfx);

            WriteCertificateFile(request.FilePath, pfx);

            // Recorded right away: a deployment interrupted later must still be able to revoke it.
            GetClientCertificatesNode(section)[request.Consumer] = new JsonObject
            {
                [ThumbprintKey] = thumbprint,
                [FileKey] = request.FilePath,
            };
            section = await SaveAsync(section, deployment, cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "Issued client certificate '{Name}' ({Thumbprint}) for '{Consumer}' with access to {Databases}.",
                request.CertificateName,
                thumbprint,
                request.Consumer,
                request.Databases.Count == 0 ? "no database" : string.Join(", ", request.Databases));

            if (previous is not null)
            {
                await RevokeAsync(server, request.Consumer, previous, cancellationToken).ConfigureAwait(false);

                if (!string.Equals(previous.File, request.FilePath, StringComparison.Ordinal))
                {
                    DeleteFile(previous.File);
                }
            }
        }

        foreach (var consumer in stale)
        {
            await RevokeAsync(server, consumer, recorded[consumer], cancellationToken).ConfigureAwait(false);
            DeleteFile(recorded[consumer].File);
            GetClientCertificatesNode(section).Remove(consumer);
        }

        if (stale.Count > 0)
        {
            await SaveAsync(section, deployment, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DestroyAsync(RavenDBCloudDeployment deployment, CancellationToken cancellationToken)
    {
        var section = await state.AcquireSectionAsync(deployment.StateSectionName, cancellationToken).ConfigureAwait(false);
        var productId = ReadString(section, ProductIdKey);
        var createdByDeployment = ReadBool(section, CreatedByDeploymentKey);
        var terminates = !deployment.Options.IsExisting &&
                         productId is not null &&
                         createdByDeployment &&
                         deployment.Options.TerminateOnDestroy;

        // The certificates of a product that keeps running are revoked; a terminated product takes them with it.
        section = await RemoveClientCertificatesAsync(deployment, section, revoke: !terminates, cancellationToken).ConfigureAwait(false);

        if (deployment.Options.IsExisting)
        {
            logger.LogInformation("RavenDB Cloud product '{Product}' is an existing product and is left running.", deployment.ProductName);
            await state.DeleteSectionAsync(section, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (productId is null)
        {
            logger.LogInformation("No RavenDB Cloud product is recorded for '{Resource}'; nothing to terminate.", deployment.Server.Name);
            return;
        }

        if (!createdByDeployment)
        {
            logger.LogWarning(
                "RavenDB Cloud product '{Product}' ({ProductId}) was not created by this deployment and is left running.",
                deployment.ProductName,
                productId);
            return;
        }

        if (!terminates)
        {
            // Aspire clears the deployment state after destroy, so a later deployment finds this product by name
            // and treats it as someone else's: from here on only the portal terminates it.
            logger.LogWarning(
                "RavenDB Cloud product '{Product}' ({ProductId}) is left running because TerminateOnDestroy is not set; " +
                "terminating deletes its data. Terminate it in the portal once it is no longer needed.",
                deployment.ProductName,
                productId);
            return;
        }

        logger.LogInformation("Terminating RavenDB Cloud product '{Product}' ({ProductId}).", deployment.ProductName, productId);

        await client.TerminateProductAsync(productId, cancellationToken).ConfigureAwait(false);
        await state.DeleteSectionAsync(section, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DeploymentStateSection> RemoveClientCertificatesAsync(
        RavenDBCloudDeployment deployment,
        DeploymentStateSection section,
        bool revoke,
        CancellationToken cancellationToken)
    {
        var recorded = ReadClientCertificates(section);

        if (recorded.Count == 0)
        {
            return section;
        }

        deployment.ProductId ??= ReadString(section, ProductIdKey);
        deployment.Endpoint.Url ??= ReadString(section, UrlKey);

        if (revoke)
        {
            try
            {
                using var server = await ConnectAsync(deployment, cancellationToken).ConfigureAwait(false);

                foreach (var (consumer, certificate) in recorded)
                {
                    await RevokeAsync(server, consumer, certificate, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception,
                    "Could not connect to RavenDB Cloud product '{Product}' to revoke the client certificates of {Consumers}. " +
                    "Remove them in the RavenDB Studio.",
                    deployment.ProductName,
                    string.Join(", ", recorded.Keys));
            }
        }

        foreach (var certificate in recorded.Values)
        {
            DeleteFile(certificate.File);
        }

        section.Data.Remove(ClientCertificatesKey);

        return await SaveAsync(section, deployment, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryReuseAsync(
        IRavenDBServerAdministration server,
        RecordedCertificate previous,
        ClientCertificateRequest request,
        IReadOnlyDictionary<string, DatabaseAccess> permissions,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(request.FilePath))
        {
            return false;
        }

        var onDisk = TryGetThumbprint(await File.ReadAllBytesAsync(request.FilePath, cancellationToken).ConfigureAwait(false));

        if (!string.Equals(onDisk, previous.Thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var current = await server.GetCertificatePermissionsAsync(previous.Thumbprint, cancellationToken).ConfigureAwait(false);

        if (current is null)
        {
            logger.LogWarning(
                "The client certificate of '{Consumer}' ({Thumbprint}) is no longer known to the server.",
                request.Consumer,
                previous.Thumbprint);
            return false;
        }

        var unchanged = current.Count == permissions.Count &&
                        permissions.All(p => current.TryGetValue(p.Key, out var access) && access == p.Value);

        if (unchanged)
        {
            logger.LogInformation("Client certificate of '{Consumer}' ({Thumbprint}) is up to date.", request.Consumer, previous.Thumbprint);
            return true;
        }

        await server.SetCertificatePermissionsAsync(previous.Thumbprint, request.CertificateName, permissions, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Client certificate of '{Consumer}' now grants access to {Databases}.",
            request.Consumer,
            permissions.Count == 0 ? "no database" : string.Join(", ", permissions.Keys));

        return true;
    }

    private async Task RevokeAsync(IRavenDBServerAdministration server, string consumer, RecordedCertificate certificate, CancellationToken cancellationToken)
    {
        try
        {
            if (await server.GetCertificatePermissionsAsync(certificate.Thumbprint, cancellationToken).ConfigureAwait(false) is null)
            {
                // Already removed, for example in the RavenDB Studio.
                return;
            }

            await server.DeleteCertificateAsync(certificate.Thumbprint, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Revoked client certificate {Thumbprint} of '{Consumer}'.", certificate.Thumbprint, consumer);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Could not revoke client certificate {Thumbprint} of '{Consumer}'. Remove it in the RavenDB Studio.",
                certificate.Thumbprint,
                consumer);
        }
    }

    private async Task<IRavenDBServerAdministration> ConnectAsync(RavenDBCloudDeployment deployment, CancellationToken cancellationToken)
    {
        var url = deployment.Endpoint.Url
            ?? throw new InvalidOperationException($"RavenDB Cloud product '{deployment.ProductName}' has not been resolved yet.");

        var certificate = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? await GetAdminCertificateAsync(deployment, cancellationToken).ConfigureAwait(false)
            : null;

        return servers.Create(url, certificate);
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

    /// <summary>
    /// The product's admin certificate, from the Cloud API. Downloaded once per deployment: the API accepts about one
    /// request per second.
    /// </summary>
    private async Task<X509Certificate2> GetAdminCertificateAsync(RavenDBCloudDeployment deployment, CancellationToken cancellationToken)
    {
        if (deployment.AdminCertificate is null)
        {
            var productId = deployment.ProductId
                ?? throw new InvalidOperationException($"RavenDB Cloud product '{deployment.ProductName}' has not been resolved yet.");
            var bundle = await client.GetClientCertificateAsync(productId, cancellationToken).ConfigureAwait(false);

            deployment.AdminCertificate = ExtractPfx(bundle)
                ?? throw new InvalidOperationException($"The certificate bundle of RavenDB Cloud product '{deployment.ProductName}' contains no .pfx file.");
        }

        return LoadCertificate(deployment.AdminCertificate);
    }

    internal static X509Certificate2 LoadCertificate(byte[] pfx) =>
#if NET9_0_OR_GREATER
        X509CertificateLoader.LoadPkcs12(pfx, password: null);
#else
        new(pfx, (string?)null);
#endif

    internal static string GetThumbprint(byte[] pfx)
    {
        using var certificate = LoadCertificate(pfx);
        return certificate.Thumbprint;
    }

    private static string? TryGetThumbprint(byte[] pfx)
    {
        try
        {
            return GetThumbprint(pfx);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static void WriteCertificateFile(string path, byte[] pfx)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        // The files hold private keys: keep them out of source control even when the artifacts are committed.
        var gitignore = Path.Combine(directory, ".gitignore");

        if (!File.Exists(gitignore))
        {
            File.WriteAllText(gitignore, "*" + Environment.NewLine);
        }

        // Overwritten in place, so a container that mounts the file sees the new certificate.
        File.WriteAllBytes(path, pfx);
    }

    private static void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private async Task<DeploymentStateSection> SaveAsync(DeploymentStateSection section, RavenDBCloudDeployment deployment, CancellationToken cancellationToken)
    {
        await state.SaveSectionAsync(section, cancellationToken).ConfigureAwait(false);
        return await state.AcquireSectionAsync(deployment.StateSectionName, cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, RecordedCertificate> ReadClientCertificates(DeploymentStateSection section)
    {
        var certificates = new Dictionary<string, RecordedCertificate>(StringComparer.Ordinal);

        if (section.Data.TryGetPropertyValue(ClientCertificatesKey, out var node) && node is JsonObject entries)
        {
            foreach (var (consumer, entry) in entries)
            {
                if (entry is JsonObject values &&
                    values[ThumbprintKey] is JsonValue thumbprint && thumbprint.TryGetValue<string>(out var thumbprintText) &&
                    values[FileKey] is JsonValue file && file.TryGetValue<string>(out var fileText))
                {
                    certificates[consumer] = new RecordedCertificate(thumbprintText, fileText);
                }
            }
        }

        return certificates;
    }

    private static JsonObject GetClientCertificatesNode(DeploymentStateSection section)
    {
        if (section.Data[ClientCertificatesKey] is not JsonObject node)
        {
            node = [];
            section.Data[ClientCertificatesKey] = node;
        }

        return node;
    }

    private sealed record RecordedCertificate(string Thumbprint, string File);

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
