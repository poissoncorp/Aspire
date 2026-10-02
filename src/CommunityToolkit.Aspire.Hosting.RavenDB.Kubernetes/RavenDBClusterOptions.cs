#pragma warning disable ASPIREATS001 // AspireExport is experimental

using Aspire.Hosting;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Kubernetes;

/// <summary>The ingress controller the RavenDB operator publishes the nodes through.</summary>
public enum RavenDBIngressController
{
    /// <summary>
    /// Traefik, the recommended one: the chart adds the routes that pass TLS through to each node, on Traefik's
    /// <c>websecure</c> entry point, so nothing has to be set up for the nodes' HTTPS and TCP addresses.
    /// </summary>
    Traefik,

    /// <summary>HAProxy Ingress. The chart only names it: the controller has to pass TLS through to the nodes.</summary>
    HAProxy,

    /// <summary>
    /// ingress-nginx, which Kubernetes has retired. The chart only names it: the controller has to pass TLS through to
    /// the nodes (<c>--enable-ssl-passthrough</c>).
    /// </summary>
    Nginx,
}

/// <summary>
/// Describes the <c>RavenDBCluster</c> the RavenDB operator runs for a server published to Kubernetes.
/// </summary>
/// <remarks>
/// The operator exposes node <c>a</c> at <c>https://a.{Domain}:443</c> through an ingress controller with TLS
/// passthrough. The node names must resolve inside the cluster as well: the operator's own bootstrap and the
/// applications connect through them.
/// </remarks>
[AspireExport(ExposeProperties = true, ExposeMethods = true)]
public sealed class RavenDBClusterOptions
{
    /// <summary>
    /// Gets or sets the domain the nodes are published under. Required.
    /// </summary>
    public string? Domain { get; set; }

    /// <summary>
    /// Gets or sets the number of nodes, tagged <c>a</c>, <c>b</c>, <c>c</c> and so on. The default is 1.
    /// </summary>
    public int Nodes { get; set; } = 1;

    /// <summary>
    /// Gets or sets the RavenDB image. The default is the image of the server resource.
    /// </summary>
    public string? Image { get; set; }

    /// <summary>
    /// Gets or sets the size of each node's data volume. The default is <c>10Gi</c>; the Kubernetes environment's
    /// <c>DefaultStorageSize</c>, meant for small volumes, does not apply.
    /// </summary>
    public string StorageSize { get; set; } = "10Gi";

    /// <summary>
    /// Gets or sets the storage class of the data volumes. The default is the Kubernetes environment's
    /// <c>DefaultStorageClassName</c>, and without one the cluster's default storage class.
    /// </summary>
    public string? StorageClassName { get; set; }

    /// <summary>
    /// Gets or sets the ingress controller the operator publishes the nodes through. The default and recommended one
    /// is <see cref="RavenDBIngressController.Traefik"/>, for which the chart also gets the routes that pass TLS
    /// through to the nodes.
    /// </summary>
    public RavenDBIngressController IngressController { get; set; } = RavenDBIngressController.Traefik;

    /// <summary>
    /// Gets or sets the name of an existing Secret that holds the license under the key <c>license.json</c>. Not
    /// needed when the server gets its license from a parameter through <c>WithLicense(...)</c>.
    /// </summary>
    public string? LicenseSecretName { get; set; }

    internal string? Mode { get; private set; }

    internal string? Email { get; private set; }

    internal string? ServerCertificateSecret { get; private set; }

    internal string? ClientCertificateSecret { get; private set; }

    internal string? CertificateAuthoritySecret { get; private set; }

    /// <summary>
    /// Secures the cluster with certificates from existing Secrets (the operator's <c>None</c> mode).
    /// </summary>
    /// <param name="serverCertificateSecret">Secret with the server certificate of every node under the key <c>server.pfx</c>.</param>
    /// <param name="clientCertificateSecret">
    /// Secret with an admin client certificate under the key <c>client.pfx</c>. The operator trusts it; the bootstrap
    /// uses it to create the databases and the applications' certificates. Applications never get it.
    /// </param>
    /// <param name="certificateAuthoritySecret">
    /// Secret with the certificate authority that issued the server certificate, under the key <c>ca.crt</c>, when it
    /// is not publicly trusted. The applications then trust it as well.
    /// </param>
    /// <returns>These options.</returns>
    public RavenDBClusterOptions WithCertificates(string serverCertificateSecret, string clientCertificateSecret, string? certificateAuthoritySecret = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverCertificateSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientCertificateSecret);

        Mode = "None";
        Email = null;
        ServerCertificateSecret = serverCertificateSecret;
        ClientCertificateSecret = clientCertificateSecret;
        CertificateAuthoritySecret = certificateAuthoritySecret;
        return this;
    }

    /// <summary>
    /// Lets the operator obtain the server certificates from Let's Encrypt. <see cref="Domain"/> must be a public
    /// domain.
    /// </summary>
    /// <param name="email">Contact address for the Let's Encrypt account.</param>
    /// <param name="clientCertificateSecret">
    /// Secret with an admin client certificate under the key <c>client.pfx</c>. The operator trusts it; the bootstrap
    /// uses it to create the databases and the applications' certificates. Applications never get it.
    /// </param>
    /// <returns>These options.</returns>
    public RavenDBClusterOptions WithLetsEncrypt(string email, string clientCertificateSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientCertificateSecret);

        Mode = "LetsEncrypt";
        Email = email;
        ServerCertificateSecret = null;
        ClientCertificateSecret = clientCertificateSecret;
        CertificateAuthoritySecret = null;
        return this;
    }
}
