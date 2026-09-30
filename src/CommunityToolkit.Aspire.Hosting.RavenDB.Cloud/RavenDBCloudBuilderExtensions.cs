#pragma warning disable ASPIREATS001 // AspireExport is experimental

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Pipelines;
using CommunityToolkit.Aspire.Hosting.RavenDB;
using CommunityToolkit.Aspire.Hosting.RavenDB.Cloud;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Hosting;

/// <summary>
/// Publishes a RavenDB server to RavenDB Cloud.
/// </summary>
public static class RavenDBCloudBuilderExtensions
{
    /// <summary>
    /// Publishes the server to a RavenDB Cloud product: <c>aspire deploy</c> finds the product by name or creates it,
    /// creates the databases declared with <c>ensureCreated</c>, and connects every consumer to it. No container is
    /// deployed for the server. <c>aspire run</c> still starts the local container.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>aspire publish</c> never calls the Cloud API: the published artifacts reference the product URL through a
    /// placeholder that <c>aspire deploy</c> fills in. <c>aspire do ravendb-cloud-provision-&lt;name&gt;</c> provisions
    /// the product without deploying the application.
    /// </para>
    /// <para>
    /// Each resource that references the server or one of its databases gets a client certificate of its own, with
    /// access to the databases it references only. In Docker Compose it is mounted as a file and passed to the
    /// RavenDB client integration through <c>Aspire:RavenDB:Client:&lt;connection name&gt;:CertificatePath</c>.
    /// </para>
    /// <para>
    /// For a product someone else owns, use <c>PublishAsExisting(url)</c> and give each application the certificate the
    /// owner issued for it instead: the API key has account-owner rights.
    /// </para>
    /// </remarks>
    /// <param name="builder">The resource builder for the RavenDB server.</param>
    /// <param name="apiKey">A secret parameter holding a RavenDB Cloud API key. The key has account-owner rights.</param>
    /// <param name="configure">Describes the product.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/> for the RavenDB server resource.</returns>
    [AspireExport(RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<RavenDBServerResource> PublishAsRavenDBCloud(
        this IResourceBuilder<RavenDBServerResource> builder,
        IResourceBuilder<ParameterResource> apiKey,
        Action<RavenDBCloudOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(apiKey);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        var options = new RavenDBCloudOptions();
        configure?.Invoke(options);

        if (options.Subdomain is { } subdomain && !RavenDBCloudDeployment.IsValidSubdomain(subdomain))
        {
            throw new ArgumentException(
                $"RavenDB Cloud does not accept the subdomain '{subdomain}': use up to {RavenDBCloudDeployment.MaxSubdomainLength} " +
                "letters, digits and dashes, not starting or ending with a dash.",
                nameof(configure));
        }

        // The API key has account-owner rights; plain HTTP is only for a local stand-in of the API.
        if (!Uri.TryCreate(options.ApiEndpoint, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttps && !endpoint.IsLoopback))
        {
            throw new ArgumentException(
                $"The RavenDB Cloud API endpoint '{options.ApiEndpoint}' must be an https URL: the API key sent to it has account-owner rights.",
                nameof(configure));
        }

        var deployment = new RavenDBCloudDeployment(
            builder.Resource,
            apiKey.Resource,
            options,
            builder.ApplicationBuilder.Environment.EnvironmentName);

        builder.ApplicationBuilder.Services.TryAddSingleton<IRavenDBCloudApiClientFactory, RavenDBCloudApiClientFactory>();
        builder.ApplicationBuilder.Services.TryAddSingleton<IRavenDBServerAdministrationFactory, RavenDBServerAdministrationFactory>();

        builder.Resource.PublishAsExternal(ReferenceExpression.Create($"{deployment.Endpoint}"), createsDatabases: true);

        // Every application that uses the server gets its client certificate as a file. The compose file refers to
        // it; the deploy step issues it and writes it next to the compose file.
        builder.ApplicationBuilder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
        {
            var all = RavenDBConsumers.Find(@event.Model, builder.Resource);
            RavenDBClientCertificates.EnsureMountable(all, builder.Resource, RavenDBClientCertificateSource.File);

            var consumers = all.Where(c => !c.BringsOwnCertificate).ToList();

            foreach (var environment in @event.Model.Resources.OfType<DockerComposeEnvironmentResource>())
            {
                builder.ApplicationBuilder.CreateResourceBuilder(environment).ConfigureComposeFile(file =>
                {
                    foreach (var consumer in consumers)
                    {
                        var certificateFile = $"./{RavenDBCloudClientCertificates.DirectoryName}/{RavenDBCloudClientCertificates.FileName(builder.Resource, consumer.Resource.Name)}";
                        RavenDBComposeCertificates.Mount(file, builder.Resource, consumer, certificateFile);
                    }
                });
            }

            return Task.CompletedTask;
        });

        return builder
            .WithAnnotation(deployment)
            .WithPipelineStepFactory(_ => RavenDBCloudPipelineSteps.Create(deployment))
            .WithPipelineConfiguration(context => RavenDBCloudPipelineSteps.Configure(deployment, context))
            .ExcludeFromManifest();
    }
}
