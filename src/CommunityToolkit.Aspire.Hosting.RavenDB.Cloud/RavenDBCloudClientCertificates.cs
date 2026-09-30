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
/// environment and the application, and as files next to the compose file, which mounts them.
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
}
