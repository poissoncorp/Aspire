#pragma warning disable ASPIREATS001 // AspireExport is experimental

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Pipelines;
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
    /// Call <see cref="RavenDBCloudOptions.AsExisting(string)"/> for a product that is managed elsewhere (typically
    /// production): the deployment then only connects to it and fails when it does not exist.
    /// </para>
    /// <para>
    /// Each resource that references the server or one of its databases gets a client certificate of its own, with
    /// access to the databases it references only. In Docker Compose it is mounted as a file and passed to the
    /// RavenDB client integration through <c>Aspire:RavenDB:Client:&lt;connection name&gt;:CertificatePath</c>.
    /// </para>
    /// </remarks>
    /// <param name="builder">The resource builder for the RavenDB server.</param>
    /// <param name="apiKey">A secret parameter holding a RavenDB Cloud API key. The key has account-owner rights.</param>
    /// <param name="configure">Describes the product.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/> for the RavenDB server resource.</returns>
    [AspireExportIgnore(Reason = "The options callback is not supported by ATS.")]
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

        var deployment = new RavenDBCloudDeployment(
            builder.Resource,
            apiKey.Resource,
            options,
            builder.ApplicationBuilder.Environment.EnvironmentName);

        builder.ApplicationBuilder.Services.TryAddSingleton<IRavenDBCloudApiClientFactory, RavenDBCloudApiClientFactory>();
        builder.ApplicationBuilder.Services.TryAddSingleton<IRavenDBServerAdministrationFactory, RavenDBServerAdministrationFactory>();

        builder.Resource.PublishAsExternal(ReferenceExpression.Create($"{deployment.Endpoint}"), createsDatabases: !options.IsExisting);

        // Every application that uses the server gets its client certificate as a file. The compose file refers to
        // it; the deploy step issues it and writes it next to the compose file.
        builder.ApplicationBuilder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
        {
            var consumers = RavenDBCloudClientCertificates.FindConsumers(@event.Model, builder.Resource);

            if (consumers.Count > 0)
            {
                foreach (var environment in @event.Model.Resources.OfType<DockerComposeEnvironmentResource>())
                {
                    builder.ApplicationBuilder.CreateResourceBuilder(environment)
                        .ConfigureComposeFile(file => RavenDBCloudClientCertificates.AddToComposeFile(file, builder.Resource, consumers));
                }
            }

            return Task.CompletedTask;
        });

        return builder
            .WithAnnotation(deployment)
            .WithPipelineStepFactory(_ => RavenDBCloudPipelineSteps.Create(deployment))
            .WithPipelineConfiguration(context => RavenDBCloudPipelineSteps.Configure(deployment, context))
            .ExcludeFromManifest();
    }

    /// <summary>
    /// Publishes the server to an existing RavenDB Cloud product: <c>aspire deploy</c> only connects the consumers
    /// to it, issuing each a client certificate, and fails when the account has no product with that name. The
    /// product and its databases are never created, changed or terminated; <c>aspire destroy</c> revokes the
    /// certificates. <c>aspire run</c> still starts the local container.
    /// </summary>
    /// <param name="builder">The resource builder for the RavenDB server.</param>
    /// <param name="apiKey">A secret parameter holding a RavenDB Cloud API key. The key has account-owner rights.</param>
    /// <param name="productName">Display name of the product in the account.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/> for the RavenDB server resource.</returns>
    [AspireExport]
    public static IResourceBuilder<RavenDBServerResource> PublishAsExistingRavenDBCloud(
        this IResourceBuilder<RavenDBServerResource> builder,
        IResourceBuilder<ParameterResource> apiKey,
        string productName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productName);

        return builder.PublishAsRavenDBCloud(apiKey, options => options.AsExisting(productName));
    }
}
