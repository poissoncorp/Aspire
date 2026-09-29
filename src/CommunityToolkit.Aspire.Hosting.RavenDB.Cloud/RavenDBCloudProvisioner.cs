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
    private const string ProductIdKey = "productId";
    private const string CreatedByDeploymentKey = "createdByDeployment";

    public async Task ProvisionAsync(RavenDBCloudDeployment deployment, CancellationToken cancellationToken)
    {
        var section = await state.AcquireSectionAsync(deployment.StateSectionName, cancellationToken).ConfigureAwait(false);
        var recordedId = ReadString(section, ProductIdKey);
        var (productId, details) = await FindProductAsync(deployment, recordedId, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Leaves every application with exactly one client certificate, with access to its databases only, in the file
    /// the deployment artifacts mount. The certificate in that file is kept while the product still knows it; any
    /// other certificate with the application's name is revoked, and so are those of applications that are no longer
    /// deployed.
    /// </summary>
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
        var registered = await server.GetCertificatesAsync(plan.NamePrefix, cancellationToken).ConfigureAwait(false);

        foreach (var request in plan.Requests)
        {
            var current = await EnsureClientCertificateAsync(server, request, registered, cancellationToken).ConfigureAwait(false);

            foreach (var other in registered.Where(c => IsNamed(c, request.CertificateName) && !IsSame(c.Thumbprint, current)))
            {
                await RevokeAsync(server, other, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var stale in registered.Where(c => plan.Requests.All(r => !IsNamed(c, r.CertificateName))))
        {
            await RevokeAsync(server, stale, cancellationToken).ConfigureAwait(false);
            DeleteFiles(deployment, plan, stale.Name[plan.NamePrefix.Length..]);
        }
    }

    public async Task DestroyAsync(RavenDBCloudDeployment deployment, ClientCertificatePlan certificates, CancellationToken cancellationToken)
    {
        var section = await state.AcquireSectionAsync(deployment.StateSectionName, cancellationToken).ConfigureAwait(false);
        var recordedId = ReadString(section, ProductIdKey);
        var (productId, details) = await FindProductAsync(deployment, recordedId, cancellationToken).ConfigureAwait(false);

        foreach (var request in certificates.Requests)
        {
            DeleteFiles(deployment, certificates, request.Consumer);
        }

        if (productId is null || details is null)
        {
            logger.LogInformation("RavenDB Cloud product '{Product}' does not exist; nothing to destroy.", deployment.ProductName);
            await state.DeleteSectionAsync(section, cancellationToken).ConfigureAwait(false);
            return;
        }

        Resolve(deployment, productId, details);
        var createdByDeployment = productId == recordedId && ReadBool(section, CreatedByDeploymentKey);

        if (createdByDeployment && deployment.Options.TerminateOnDestroy)
        {
            // The certificates go with the product.
            logger.LogInformation("Terminating RavenDB Cloud product '{Product}' ({ProductId}).", deployment.ProductName, productId);

            await client.TerminateProductAsync(productId, cancellationToken).ConfigureAwait(false);
            await state.DeleteSectionAsync(section, cancellationToken).ConfigureAwait(false);
            return;
        }

        await RevokeClientCertificatesAsync(deployment, certificates, cancellationToken).ConfigureAwait(false);

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
    }

    /// <returns>The thumbprint of the application's certificate.</returns>
    private async Task<string> EnsureClientCertificateAsync(
        IRavenDBServerAdministration server,
        ClientCertificateRequest request,
        IReadOnlyList<RegisteredCertificate> registered,
        CancellationToken cancellationToken)
    {
        var permissions = request.Databases.ToDictionary(d => d, _ => DatabaseAccess.ReadWrite, StringComparer.OrdinalIgnoreCase);
        var onDisk = File.Exists(request.FilePath)
            ? TryGetThumbprint(await File.ReadAllBytesAsync(request.FilePath, cancellationToken).ConfigureAwait(false))
            : null;

        if (registered.FirstOrDefault(c => IsNamed(c, request.CertificateName) && IsSame(c.Thumbprint, onDisk)) is { } current)
        {
            if (GrantsExactly(current, permissions))
            {
                logger.LogInformation("Client certificate of '{Consumer}' ({Thumbprint}) is up to date.", request.Consumer, current.Thumbprint);
            }
            else
            {
                await server.SetCertificatePermissionsAsync(current.Thumbprint, request.CertificateName, permissions, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Client certificate of '{Consumer}' now grants access to {Databases}.", request.Consumer, Describe(request.Databases));
            }

            return current.Thumbprint;
        }

        var bundle = await server.CreateClientCertificateAsync(request.CertificateName, permissions, cancellationToken).ConfigureAwait(false);
        var pfx = ExtractPfx(bundle)
            ?? throw new InvalidOperationException($"The certificate RavenDB issued for '{request.Consumer}' contains no .pfx file.");
        var thumbprint = GetThumbprint(pfx);

        WriteCertificateFile(request.FilePath, pfx);

        logger.LogInformation(
            "Issued client certificate '{Name}' ({Thumbprint}) for '{Consumer}' with access to {Databases}.",
            request.CertificateName,
            thumbprint,
            request.Consumer,
            Describe(request.Databases));

        return thumbprint;
    }

    /// <summary>Revokes every certificate the deployment issued on a product that keeps running.</summary>
    private async Task RevokeClientCertificatesAsync(RavenDBCloudDeployment deployment, ClientCertificatePlan certificates, CancellationToken cancellationToken)
    {
        if (!IsSecured(deployment))
        {
            return;
        }

        try
        {
            using var server = await ConnectAsync(deployment, cancellationToken).ConfigureAwait(false);

            foreach (var certificate in await server.GetCertificatesAsync(certificates.NamePrefix, cancellationToken).ConfigureAwait(false))
            {
                await RevokeAsync(server, certificate, cancellationToken).ConfigureAwait(false);
            }
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

    private async Task RevokeAsync(IRavenDBServerAdministration server, RegisteredCertificate certificate, CancellationToken cancellationToken)
    {
        try
        {
            await server.DeleteCertificateAsync(certificate.Thumbprint, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Revoked client certificate '{Name}' ({Thumbprint}).", certificate.Name, certificate.Thumbprint);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Could not revoke client certificate '{Name}' ({Thumbprint}). Remove it in the RavenDB Studio.",
                certificate.Name,
                certificate.Thumbprint);
        }
    }

    private async Task<IRavenDBServerAdministration> ConnectAsync(RavenDBCloudDeployment deployment, CancellationToken cancellationToken)
    {
        var url = deployment.Endpoint.Url
            ?? throw new InvalidOperationException($"RavenDB Cloud product '{deployment.ProductName}' has not been resolved yet.");

        var certificate = IsSecured(deployment)
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
            SubdomainName: deployment.Subdomain,
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

    private static void DeleteFiles(RavenDBCloudDeployment deployment, ClientCertificatePlan plan, string consumer)
    {
        foreach (var directory in plan.Directories)
        {
            var path = Path.Combine(directory, RavenDBCloudClientCertificates.FileName(deployment.Server, consumer));

            if (File.Exists(path))
            {
                File.Delete(path);
            }
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

    private static bool IsSecured(RavenDBCloudDeployment deployment) =>
        deployment.Endpoint.Url?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsNamed(RegisteredCertificate certificate, string name) =>
        string.Equals(certificate.Name, name, StringComparison.OrdinalIgnoreCase);

    private static bool IsSame(string thumbprint, string? other) =>
        string.Equals(thumbprint, other, StringComparison.OrdinalIgnoreCase);

    private static bool GrantsExactly(RegisteredCertificate certificate, IReadOnlyDictionary<string, DatabaseAccess> permissions) =>
        certificate.Permissions.Count == permissions.Count &&
        permissions.All(p => certificate.Permissions.TryGetValue(p.Key, out var access) && access == p.Value);

    private static string Describe(IReadOnlyList<string> databases) =>
        databases.Count == 0 ? "no database" : string.Join(", ", databases);

    private static bool IsGone(string? status) =>
        status is ProductStatus.Terminated or ProductStatus.Terminating;

    private static string? ReadString(DeploymentStateSection section, string key) =>
        section.Data.TryGetPropertyValue(key, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool ReadBool(DeploymentStateSection section, string key) =>
        section.Data.TryGetPropertyValue(key, out var node) && node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
}
