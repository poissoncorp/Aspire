using Aspire.Hosting.Kubernetes.Resources;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Kubernetes;

/// <summary>
/// The <c>RavenDBCluster</c> custom resource of the RavenDB operator (<c>ravendb.ravendb.io/v1</c>).
/// </summary>
internal sealed class RavenDBClusterManifest() : BaseKubernetesResource("ravendb.ravendb.io/v1", "RavenDBCluster")
{
    public RavenDBClusterSpec Spec { get; set; } = new();
}

internal sealed class RavenDBClusterSpec
{
    public string Image { get; set; } = string.Empty;

    public string ImagePullPolicy { get; set; } = "IfNotPresent";

    public string Mode { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string Domain { get; set; } = string.Empty;

    public string LicenseSecretRef { get; set; } = string.Empty;

    public string ClientCertSecretRef { get; set; } = string.Empty;

    public string? ClusterCertSecretRef { get; set; }

    public string? CaCertSecretRef { get; set; }

    public List<RavenDBClusterNode> Nodes { get; set; } = [];

    public RavenDBClusterStorage Storage { get; set; } = new();

    public RavenDBClusterExternalAccess ExternalAccessConfiguration { get; set; } = new();
}

internal sealed class RavenDBClusterNode
{
    public string Tag { get; set; } = string.Empty;

    public string PublicServerUrl { get; set; } = string.Empty;

    public string PublicServerUrlTcp { get; set; } = string.Empty;
}

internal sealed class RavenDBClusterStorage
{
    public RavenDBClusterVolume Data { get; set; } = new();
}

internal sealed class RavenDBClusterVolume
{
    public string Size { get; set; } = string.Empty;

    public string? StorageClassName { get; set; }
}

internal sealed class RavenDBClusterExternalAccess
{
    public string Type { get; set; } = "ingress-controller";

    public RavenDBClusterIngressController IngressControllerContext { get; set; } = new();
}

internal sealed class RavenDBClusterIngressController
{
    public string IngressClassName { get; set; } = "nginx";
}

/// <summary>
/// A Traefik <c>IngressRouteTCP</c> (<c>traefik.io/v1alpha1</c>) that passes TLS through to the nodes.
/// </summary>
internal sealed class TraefikIngressRouteTcp() : BaseKubernetesResource("traefik.io/v1alpha1", "IngressRouteTCP")
{
    public TraefikIngressRouteTcpSpec Spec { get; set; } = new();
}

internal sealed class TraefikIngressRouteTcpSpec
{
    public List<string> EntryPoints { get; set; } = [];

    public List<TraefikRoute> Routes { get; set; } = [];

    public TraefikTls Tls { get; set; } = new();
}

internal sealed class TraefikRoute
{
    public string Match { get; set; } = string.Empty;

    public List<TraefikRouteService> Services { get; set; } = [];
}

internal sealed class TraefikRouteService
{
    public string Name { get; set; } = string.Empty;

    public int Port { get; set; }
}

internal sealed class TraefikTls
{
    public bool Passthrough { get; set; } = true;
}

/// <summary>
/// A <c>batch/v1</c> Job. Aspire's Kubernetes publisher models Deployments and StatefulSets only, and either would
/// restart a one-shot pod forever.
/// </summary>
internal sealed class KubernetesJob() : Workload("batch/v1", "Job")
{
    public KubernetesJobSpec Spec { get; set; } = new();

    public override PodTemplateSpecV1 PodTemplate => Spec.Template;
}

internal sealed class KubernetesJobSpec
{
    public int BackoffLimit { get; set; } = 10;

    /// <summary>
    /// A bootstrap that hangs is failed after half an hour, longer than it waits for the cluster: the next deployment
    /// then runs it again.
    /// </summary>
    public int ActiveDeadlineSeconds { get; set; } = 1800;

    public PodTemplateSpecV1 Template { get; set; } = new();
}

internal sealed class KubernetesServiceAccount() : BaseKubernetesResource("v1", "ServiceAccount");
