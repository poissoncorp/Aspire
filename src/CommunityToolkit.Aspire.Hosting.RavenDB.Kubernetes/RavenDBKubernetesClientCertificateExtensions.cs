#pragma warning disable ASPIREATS001 // AspireExport is experimental

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;
using CommunityToolkit.Aspire.Hosting.RavenDB;
using CommunityToolkit.Aspire.Hosting.RavenDB.Kubernetes;
using CommunityToolkit.Aspire.Utils;

namespace Aspire.Hosting;

/// <summary>
/// Gives applications deployed to Kubernetes the RavenDB client certificates that the owner of a server issued for
/// them.
/// </summary>
public static class RavenDBKubernetesClientCertificateExtensions
{
    /// <summary>
    /// Gives the application a client certificate that the owner of the server issued for it, typically for a server
    /// published with <see cref="RavenDBBuilderExtensions.PublishAsExisting"/>. In Kubernetes the Secret is mounted into
    /// the application's pods and passed to the RavenDB client integration through
    /// <c>Aspire:RavenDB:Client:&lt;connection name&gt;:CertificatePath</c>. The deployment issues no certificate of its
    /// own to the application.
    /// </summary>
    /// <typeparam name="T">The application resource.</typeparam>
    /// <param name="builder">The resource builder for the application.</param>
    /// <param name="server">
    /// The server the certificate is for; it serves every connection of the application to that server. The
    /// application references the server or one of its databases.
    /// </param>
    /// <param name="secretName">Existing Secret with the certificate, without a password, under the key <c>client.pfx</c>.</param>
    /// <param name="certificateAuthoritySecret">
    /// Existing Secret with the certificate authority of the server certificate under the key <c>ca.crt</c>, when it is
    /// not publicly trusted. The application trusts it next to the image's own roots.
    /// </param>
    /// <returns>The <see cref="IResourceBuilder{T}"/> for the application.</returns>
    [AspireExport("withRavenDBServerClientCertificateSecret", MethodName = "withRavenDBClientCertificateSecret")]
    public static IResourceBuilder<T> WithRavenDBClientCertificateSecret<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<RavenDBServerResource> server,
        string secretName,
        string? certificateAuthoritySecret = null)
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);
        MatchingPackageVersion.Ensure(typeof(RavenDBServerResource), typeof(RavenDBClusterOptions));

        return builder.WithClientCertificateSecret(server.Resource, secretName, certificateAuthoritySecret);
    }

    /// <inheritdoc cref="WithRavenDBClientCertificateSecret{T}(IResourceBuilder{T}, IResourceBuilder{RavenDBServerResource}, string, string?)"/>
    /// <param name="builder">The resource builder for the application.</param>
    /// <param name="database">A database of the server the certificate is for.</param>
    /// <param name="secretName">Existing Secret with the certificate under the key <c>client.pfx</c>.</param>
    /// <param name="certificateAuthoritySecret">Existing Secret with the server's certificate authority under the key <c>ca.crt</c>.</param>
    [AspireExportIgnore(Reason = "Polyglot app hosts pass the server: ATS allows one export per member name and target type.")]
    public static IResourceBuilder<T> WithRavenDBClientCertificateSecret<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<RavenDBDatabaseResource> database,
        string secretName,
        string? certificateAuthoritySecret = null)
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);
        MatchingPackageVersion.Ensure(typeof(RavenDBServerResource), typeof(RavenDBClusterOptions));

        return builder.WithClientCertificateSecret(database.Resource.Parent, secretName, certificateAuthoritySecret);
    }

    private static IResourceBuilder<T> WithClientCertificateSecret<T>(
        this IResourceBuilder<T> builder,
        RavenDBServerResource server,
        string secretName,
        string? certificateAuthoritySecret)
        where T : IComputeResource
    {
        builder.WithAnnotation(RavenDBClientCertificates.Single(
            builder.Resource,
            new RavenDBClientCertificateAnnotation(server, RavenDBClientCertificateSource.KubernetesSecret, secretName, certificateAuthoritySecret)));

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        // Once the model is complete: the connections the certificate serves may be declared after this call.
        builder.ApplicationBuilder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
        {
            var consumer = RavenDBConsumers.Find(@event.Model, server).FirstOrDefault(c => ReferenceEquals(c.Resource, builder.Resource));

            if (consumer is not null && @event.Model.Resources.OfType<KubernetesEnvironmentResource>().Any())
            {
                builder.PublishAsKubernetesService(k8s => RavenDBKubernetesCertificates.Mount(k8s, server, consumer, secretName, certificateAuthoritySecret));
            }

            return Task.CompletedTask;
        });

        return builder;
    }
}
