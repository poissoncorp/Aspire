using System.Globalization;
using Aspire.Hosting.ApplicationModel;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Kubernetes;

/// <summary>
/// Attached to a <see cref="RavenDBServerResource"/> published to Kubernetes: the cluster the RavenDB operator runs
/// for it, and the names of the objects the published chart contains.
/// </summary>
internal sealed class RavenDBClusterDeployment(RavenDBServerResource server, RavenDBClusterOptions options) : IResourceAnnotation
{
    public RavenDBServerResource Server { get; } = server;

    public RavenDBClusterOptions Options { get; } = options;

    /// <summary>Public URLs of the cluster's nodes.</summary>
    public IReadOnlyList<string> NodeUrls => [.. Enumerable.Range(0, Options.Nodes).Select(i => $"https://{NodeHost(i)}:443")];

    /// <summary>The host name of a node's HTTPS endpoint.</summary>
    public string NodeHost(int index) => $"{NodeTag(index)}.{Options.Domain}";

    /// <summary>
    /// The host name of a node's TCP endpoint. It shares port 443 with HTTPS: the ingress controller tells them apart
    /// by the host name the client asks for.
    /// </summary>
    public string NodeTcpHost(int index) => $"{NodeTag(index)}-tcp.{Options.Domain}";

    /// <summary>The URL the applications connect to.</summary>
    public ReferenceExpression Url => ReferenceExpression.Create($"{NodeUrls[0]}");

    /// <summary>The Secret with the admin certificate; <c>PublishAsRavenDBCluster</c> rejects options without one.</summary>
    public string ClientCertificateSecret => Options.ClientCertificateSecret!;

    public string? CertificateAuthoritySecret => Options.CertificateAuthoritySecret;

    /// <summary>Applications the bootstrap issues a certificate to, known once the model is complete.</summary>
    public IReadOnlyList<RavenDBConsumer> Consumers { get; set; } = [];

    /// <summary>Name of the bootstrap Job, known once the chart has been written.</summary>
    public string? BootstrapJobName { get; set; }

    public string ResourceName => RavenDBKubernetesCertificates.NameOf(Server);

    public string BootstrapName => $"{ResourceName}-bootstrap";

    public string ScriptConfigMapName => $"{BootstrapName}-script";

    public string LicenseSecretName => Options.LicenseSecretName ?? $"{ResourceName}-license";

    public string ApplicationSecretName(IResource consumer) => $"{ResourceName}-{consumer.Name.ToLowerInvariant()}-client-certificate";

    public static string NodeTag(int index) =>
        index < 26
            ? ((char)('a' + index)).ToString()
            : "n" + index.ToString(CultureInfo.InvariantCulture);
}
