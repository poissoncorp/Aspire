using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Raven.Client.ServerWide.Operations.Certificates;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Cloud;

/// <summary>
/// The applications' client certificates on a product's server, reached with its admin certificate, and the files
/// the deployment artifacts mount them from.
/// </summary>
internal sealed partial class RavenDBCloudCertificateReconciler(ILogger logger)
{
    /// <summary>
    /// Leaves every application with exactly one client certificate, with access to its databases only, in the file
    /// the deployment artifacts mount. The certificate in that file is kept while the product still knows it; any
    /// other certificate with the application's name is revoked, and so are those of applications that are no longer
    /// deployed.
    /// </summary>
    public async Task ReconcileAsync(
        IRavenDBServerAdministration server,
        RavenDBCloudDeployment deployment,
        ClientCertificatePlan plan,
        CancellationToken cancellationToken)
    {
        var registered = await server.GetCertificatesAsync(plan.NamePrefix, cancellationToken).ConfigureAwait(false);

        foreach (var request in plan.Requests)
        {
            var current = await EnsureAsync(server, request, registered, cancellationToken).ConfigureAwait(false);

            foreach (var other in registered.Where(c => IsNamed(c, request.CertificateName) && !IsSame(c.Thumbprint, current)))
            {
                await RevokeAsync(server, other, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var stale in registered.Where(c => plan.Requests.All(r => !IsNamed(c, r.CertificateName))))
        {
            await RevokeAsync(server, stale, cancellationToken).ConfigureAwait(false);

            // The name comes from the server: only a resource name maps to a file of ours.
            if (ResourceNamePattern().IsMatch(stale.Name[plan.NamePrefix.Length..]))
            {
                DeleteFiles(deployment, plan, stale.Name[plan.NamePrefix.Length..]);
            }
        }
    }

    /// <summary>Revokes every certificate the deployment issued, for a product that keeps running.</summary>
    public async Task RevokeAllAsync(IRavenDBServerAdministration server, ClientCertificatePlan plan, CancellationToken cancellationToken)
    {
        foreach (var certificate in await server.GetCertificatesAsync(plan.NamePrefix, cancellationToken).ConfigureAwait(false))
        {
            await RevokeAsync(server, certificate, cancellationToken).ConfigureAwait(false);
        }
    }

    public static void DeleteFiles(RavenDBCloudDeployment deployment, ClientCertificatePlan plan, string consumer)
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

    /// <returns>The thumbprint of the application's certificate.</returns>
    private async Task<string> EnsureAsync(
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
            if (IsUpToDate(current, permissions))
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
        var pfx = RavenDBCloudClientCertificates.ExtractPfx(bundle)
            ?? throw new InvalidOperationException($"The certificate RavenDB issued for '{request.Consumer}' contains no .pfx file.");
        var thumbprint = RavenDBCloudClientCertificates.GetThumbprint(pfx);

        WriteCertificateFile(request.FilePath, pfx);

        logger.LogInformation(
            "Issued client certificate '{Name}' ({Thumbprint}) for '{Consumer}' with access to {Databases}.",
            request.CertificateName,
            thumbprint,
            request.Consumer,
            Describe(request.Databases));

        return thumbprint;
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

    private static void WriteCertificateFile(string path, byte[] pfx)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        // Only the deploying user may enter the directory. The files keep the default mode: the application's
        // container reads them through the compose mount, with a user of its own.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // The files hold private keys: keep them out of source control even when the artifacts are committed.
        var gitignore = Path.Combine(directory, ".gitignore");

        if (!File.Exists(gitignore))
        {
            File.WriteAllText(gitignore, "*" + Environment.NewLine);
        }

        // Overwritten in place, so a container that mounts the file sees the new certificate.
        File.WriteAllBytes(path, pfx);
    }

    private static string? TryGetThumbprint(byte[] pfx)
    {
        try
        {
            return RavenDBCloudClientCertificates.GetThumbprint(pfx);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static bool IsNamed(RegisteredCertificate certificate, string name) =>
        string.Equals(certificate.Name, name, StringComparison.OrdinalIgnoreCase);

    private static bool IsSame(string thumbprint, string? other) =>
        string.Equals(thumbprint, other, StringComparison.OrdinalIgnoreCase);

    // Clearance included: a certificate raised to Operator in the Studio goes back to ValidUser.
    private static bool IsUpToDate(RegisteredCertificate certificate, IReadOnlyDictionary<string, DatabaseAccess> permissions) =>
        certificate.Clearance == SecurityClearance.ValidUser &&
        certificate.Permissions.Count == permissions.Count &&
        permissions.All(p => certificate.Permissions.TryGetValue(p.Key, out var access) && access == p.Value);

    private static string Describe(IReadOnlyList<string> databases) =>
        databases.Count == 0 ? "no database" : string.Join(", ", databases);

    [GeneratedRegex("^[A-Za-z0-9-]+$")]
    private static partial Regex ResourceNamePattern();
}
