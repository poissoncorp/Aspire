#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;

namespace CommunityToolkit.Aspire.Hosting.RavenDB;

/// <summary>
/// The steps of Aspire's compute environments that the RavenDB steps are ordered against. Aspire names them by
/// convention only, so the names live here: a rename in Aspire shows up in one place, and a missing step stops the
/// pipeline instead of leaving the RavenDB steps unordered.
/// </summary>
internal static class AspireSteps
{
    /// <summary>Writes an environment's artifacts.</summary>
    public static string Publish(IResource environment) => $"publish-{environment.Name}";

    /// <summary>Resolves an environment's parameters and images into its artifacts.</summary>
    public static string Prepare(IResource environment) => $"prepare-{environment.Name}";

    /// <summary>Starts a Docker Compose environment.</summary>
    public static string ComposeUp(IResource environment) => $"docker-compose-up-{environment.Name}";

    /// <summary>Stops a Docker Compose environment on destroy.</summary>
    public static string ComposeDown(IResource environment) => $"destroy-compose-{environment.Name}";

    /// <summary>Installs a Kubernetes environment's chart.</summary>
    public static string HelmDeploy(IResource environment) => $"helm-deploy-{environment.Name}";

    /// <summary>The step, which Aspire registers for every operation (publish, deploy, destroy).</summary>
    public static PipelineStep Required(PipelineConfigurationContext context, string name) =>
        context.Steps.FirstOrDefault(s => s.Name == name)
        ?? throw new DistributedApplicationException(
            $"Aspire has no pipeline step '{name}', which the RavenDB integration orders its own steps against. This " +
            "version of the integration does not support the Aspire version in use.");
}
