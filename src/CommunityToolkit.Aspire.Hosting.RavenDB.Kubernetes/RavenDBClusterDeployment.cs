using System.Globalization;
using Aspire.Hosting.ApplicationModel;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Kubernetes;

/// <summary>
/// Attached to a <see cref="RavenDBServerResource"/> published to Kubernetes: the cluster the RavenDB operator runs
/// for it (or the existing server it connects to), and the names of the objects the published chart contains.
/// </summary>
internal sealed class RavenDBClusterDeployment : IResourceAnnotation
{
    private RavenDBClusterDeployment(
        RavenDBServerResource server,
        RavenDBClusterOptions? options,
        ReferenceExpression url,
        IReadOnlyList<string> nodeUrls,
        string clientCertificateSecret,
        string? certificateAuthoritySecret)
    {
        Server = server;
        Options = options;
        Url = url;
        NodeUrls = nodeUrls;
        ClientCertificateSecret = clientCertificateSecret;
        CertificateAuthoritySecret = certificateAuthoritySecret;
    }

    /// <summary>A cluster the RavenDB operator runs from the published <c>RavenDBCluster</c>.</summary>
    public static RavenDBClusterDeployment ForCluster(RavenDBServerResource server, RavenDBClusterOptions options)
    {
        var nodeUrls = Enumerable.Range(0, options.Nodes)
            .Select(i => $"https://{NodeTag(i)}.{options.Domain}:443")
            .ToList();

        return new RavenDBClusterDeployment(
            server,
            options,
            ReferenceExpression.Create($"{nodeUrls[0]}"),
            nodeUrls,
            options.ClientCertificateSecret!,
            options.CertificateAuthoritySecret);
    }

    /// <summary>A server that runs elsewhere; the chart only connects the applications to it.</summary>
    public static RavenDBClusterDeployment ForExisting(
        RavenDBServerResource server,
        ParameterResource url,
        string clientCertificateSecret,
        string? certificateAuthoritySecret) =>
        new(server, options: null, ReferenceExpression.Create($"{url}"), nodeUrls: [], clientCertificateSecret, certificateAuthoritySecret);

    public RavenDBServerResource Server { get; }

    /// <summary>The cluster to run, or <see langword="null"/> for an existing server.</summary>
    public RavenDBClusterOptions? Options { get; }

    public bool IsExisting => Options is null;

    /// <summary>The URL the applications and the bootstrap connect to.</summary>
    public ReferenceExpression Url { get; }

    /// <summary>Public URLs of the cluster's nodes; empty for an existing server.</summary>
    public IReadOnlyList<string> NodeUrls { get; }

    public string ClientCertificateSecret { get; }

    public string? CertificateAuthoritySecret { get; }

    /// <summary>Applications that reference the server, known once the model is complete.</summary>
    public IReadOnlyList<RavenDBConsumer> Consumers { get; set; } = [];

    /// <summary>Name of the bootstrap Job, known once the chart has been written.</summary>
    public string? BootstrapJobName { get; set; }

    public string ResourceName => ToKubernetesName(Server.Name);

    public string BootstrapName => $"{ResourceName}-bootstrap";

    public string ScriptConfigMapName => $"{BootstrapName}-script";

    public string LicenseSecretName => Options?.LicenseSecretName ?? $"{ResourceName}-license";

    public string ApplicationSecretName(IResource consumer) => $"{ResourceName}-{ToKubernetesName(consumer.Name)}-client-certificate";

    /// <summary>Directory the application's certificate is mounted in.</summary>
    public string CertificateDirectory => $"/ravendb/{ResourceName}";

    public string CertificatePath => $"{CertificateDirectory}/client.pfx";

    public string CertificateAuthorityDirectory => $"/ravendb/{ResourceName}-ca";

    public static string NodeTag(int index) =>
        index < 26
            ? ((char)('a' + index)).ToString()
            : "n" + index.ToString(CultureInfo.InvariantCulture);

    private static string ToKubernetesName(string name) => name.ToLowerInvariant();
}
