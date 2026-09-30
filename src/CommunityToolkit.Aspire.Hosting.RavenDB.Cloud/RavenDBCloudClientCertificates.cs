using System.IO.Compression;
using System.Security.Cryptography.X509Certificates;
using Aspire.Hosting.ApplicationModel;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Cloud;

/// <summary>A client certificate the deployment issues to an application and the file it writes it to.</summary>
internal sealed record ClientCertificateRequest(string Consumer, string CertificateName, IReadOnlyList<string> Databases, string FilePath);

/// <summary>
/// The client certificates of a product's applications: the prefix every certificate the deployment issues is named
/// with, the directories their files live in, and one request per application deployed now.
/// </summary>
internal sealed record ClientCertificatePlan(string NamePrefix, IReadOnlyList<string> Directories, IReadOnlyList<ClientCertificateRequest> Requests);

/// <summary>
/// Where the client certificates the deployment issues live: on the product under a name made of the AppHost, the
/// environment and the application, and as files next to the compose file, which mounts them. Also reads the .pfx
/// files RavenDB hands out.
/// </summary>
internal static class RavenDBCloudClientCertificates
{
    public const string DirectoryName = "ravendb-certs";

    // Dots separate the parts, so a dotted AppHost name (Contoso.AppHost) or environment must not add parts of its
    // own: "a.b" and "c" would otherwise share a prefix with "a" and "b.c".
    public static string NamePrefix(string appHost, string environment) =>
        $"aspire.{appHost.Replace('.', '-')}.{environment.Replace('.', '-')}.".ToLowerInvariant();

    // Resource names never contain "--", so it tells the server's name from the application's.
    public static string FileName(RavenDBServerResource server, string consumer) => $"{server.Name}--{consumer}.pfx".ToLowerInvariant();

    /// <summary>The certificate comes as a zip bundle (pfx and pem files) or as a bare pfx.</summary>
    public static byte[]? ExtractPfx(byte[] bundle)
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

    public static X509Certificate2 Load(byte[] pfx) =>
#if NET9_0_OR_GREATER
        X509CertificateLoader.LoadPkcs12(pfx, password: null);
#else
        new(pfx, (string?)null);
#endif

    public static string GetThumbprint(byte[] pfx)
    {
        using var certificate = Load(pfx);
        return certificate.Thumbprint;
    }
}
