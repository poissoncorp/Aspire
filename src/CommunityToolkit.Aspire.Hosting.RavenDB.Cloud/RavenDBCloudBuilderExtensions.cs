#pragma warning disable ASPIREATS001 // AspireExport is experimental

using Aspire.Hosting.ApplicationModel;
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

        builder.Resource.PublishAsExternal(ReferenceExpression.Create($"{deployment.Endpoint}"), createsDatabases: !options.IsExisting);

        return builder
            .WithAnnotation(deployment)
            .WithPipelineStepFactory(_ => RavenDBCloudPipelineSteps.Create(deployment))
            .WithPipelineConfiguration(context => RavenDBCloudPipelineSteps.Configure(deployment, context))
            .ExcludeFromManifest();
    }

    /// <summary>
    /// Publishes the server to an existing RavenDB Cloud product: <c>aspire deploy</c> only connects the consumers
    /// to it, and fails when the account has no product with that name. The product is never created, changed or
    /// terminated. <c>aspire run</c> still starts the local container.
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
