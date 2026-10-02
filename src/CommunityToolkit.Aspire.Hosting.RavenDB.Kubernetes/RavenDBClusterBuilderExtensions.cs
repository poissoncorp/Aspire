#pragma warning disable ASPIREATS001 // AspireExport is experimental

using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using CommunityToolkit.Aspire.Hosting.RavenDB.Kubernetes;
using CommunityToolkit.Aspire.Utils;

namespace Aspire.Hosting;

/// <summary>
/// Publishes a RavenDB server to Kubernetes through the RavenDB operator.
/// </summary>
public static partial class RavenDBClusterBuilderExtensions
{
    /// <summary>
    /// Publishes the server as a cluster the RavenDB operator runs: the Helm chart gets a <c>RavenDBCluster</c> and a
    /// bootstrap Job that creates the databases declared with <c>ensureCreated</c> and gives every application a client
    /// certificate for its databases only. <c>aspire run</c> still starts the local container.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The RavenDB operator must be installed in the cluster. The certificates the operator needs come from existing
    /// Secrets (see <see cref="RavenDBClusterOptions.WithCertificates(string, string, string?)"/>), the license from
    /// <c>WithLicense(parameter)</c> or an existing Secret.
    /// </para>
    /// <para>
    /// Applications get their certificate as a mounted file, passed to the RavenDB client integration through
    /// <c>Aspire:RavenDB:Client:&lt;connection name&gt;:CertificatePath</c>. <c>aspire deploy</c> waits for the
    /// bootstrap to complete.
    /// </para>
    /// </remarks>
    /// <param name="builder">The resource builder for the RavenDB server.</param>
    /// <param name="configure">Describes the cluster. <see cref="RavenDBClusterOptions.Domain"/> and the certificates are required.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/> for the RavenDB server resource.</returns>
    [AspireExport(RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<RavenDBServerResource> PublishAsRavenDBCluster(
        this IResourceBuilder<RavenDBServerResource> builder,
        Action<RavenDBClusterOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        MatchingPackageVersion.Ensure(typeof(RavenDBServerResource), typeof(RavenDBClusterOptions));

        var options = new RavenDBClusterOptions();
        configure(options);

        if (string.IsNullOrWhiteSpace(options.Domain))
        {
            throw new ArgumentException("A RavenDB cluster needs a Domain: node a is published at https://a.<domain>.", nameof(configure));
        }

        // The domain ends up in the node URLs, the certificates and the ingress routes.
        if (!DomainPattern().IsMatch(options.Domain))
        {
            throw new ArgumentException(
                $"'{options.Domain}' is not a domain the nodes can be published under: use lowercase letters, digits, " +
                "dashes and dots, as in ravendb.example.com.",
                nameof(configure));
        }

        // Node tags are the letters a to z.
        if (options.Nodes is < 1 or > 26)
        {
            throw new ArgumentException("A RavenDB cluster has 1 to 26 nodes.", nameof(configure));
        }

        if (!Enum.IsDefined(options.IngressController))
        {
            throw new ArgumentException(
                $"The RavenDB operator publishes the nodes through Traefik, HAProxy or ingress-nginx, not '{options.IngressController}'.",
                nameof(configure));
        }

        if (options.Mode is null)
        {
            throw new ArgumentException(
                "A RavenDB cluster needs certificates: call WithCertificates(...) with existing Secrets, or WithLetsEncrypt(...).",
                nameof(configure));
        }

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        RavenDBClusterPublishing.Configure(builder, new RavenDBClusterDeployment(builder.Resource, options));
        return builder;
    }

    // DNS labels of lowercase letters, digits and inner dashes, separated by dots.
    [GeneratedRegex(@"^(?=.{1,253}$)[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$")]
    private static partial Regex DomainPattern();
}
