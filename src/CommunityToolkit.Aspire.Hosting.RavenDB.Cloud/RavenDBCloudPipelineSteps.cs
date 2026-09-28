using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Cloud;

/// <summary>
/// The deploy and destroy steps of a server published to RavenDB Cloud. Their names are part of the public
/// contract: <c>aspire do ravendb-cloud-provision-&lt;name&gt;</c> provisions the product without deploying anything else.
/// </summary>
internal static class RavenDBCloudPipelineSteps
{
    public static string ProvisionStepName(RavenDBServerResource server) => $"ravendb-cloud-provision-{server.Name}";

    public static string DatabasesStepName(RavenDBServerResource server) => $"ravendb-cloud-databases-{server.Name}";

    public static string ConfigureStepName(RavenDBServerResource server) => $"ravendb-cloud-configure-{server.Name}";

    public static string DestroyStepName(RavenDBServerResource server) => $"ravendb-cloud-destroy-{server.Name}";

    public static IEnumerable<PipelineStep> Create(RavenDBCloudDeployment deployment)
    {
        var server = deployment.Server;

        var provision = new PipelineStep
        {
            Name = ProvisionStepName(server),
            Description = $"Finds or creates the RavenDB Cloud product '{deployment.ProductName}' and waits until it is active",
            Resource = server,
            Tags = [WellKnownPipelineTags.ProvisionInfrastructure],
            Action = async context =>
            {
                using var client = await CreateClientAsync(deployment, context).ConfigureAwait(false);
                await CreateProvisioner(client, context).ProvisionAsync(deployment, context.CancellationToken).ConfigureAwait(false);

                context.Summary.Add($"RavenDB Cloud ({server.Name})", deployment.Endpoint.Url!);
            },
        };
        provision.DependsOn(WellKnownPipelineSteps.DeployPrereq);
        provision.RequiredBy(WellKnownPipelineSteps.Deploy);

        var databases = new PipelineStep
        {
            Name = DatabasesStepName(server),
            Description = $"Creates the databases of '{server.Name}' in RavenDB Cloud",
            Resource = server,
            Action = async context =>
            {
                using var client = await CreateClientAsync(deployment, context).ConfigureAwait(false);
                await CreateProvisioner(client, context).EnsureDatabasesAsync(deployment, context.CancellationToken).ConfigureAwait(false);
            },
        };
        databases.DependsOn(provision);
        databases.RequiredBy(WellKnownPipelineSteps.Deploy);

        var configure = new PipelineStep
        {
            Name = ConfigureStepName(server),
            Description = $"Writes the RavenDB Cloud URL of '{server.Name}' into the generated environment files",
            Resource = server,
            Action = context => PatchEnvironmentFilesAsync(deployment, context),
        };
        configure.DependsOn(provision);
        configure.RequiredBy(WellKnownPipelineSteps.Deploy);

        var destroy = new PipelineStep
        {
            Name = DestroyStepName(server),
            Description = $"Terminates the RavenDB Cloud product '{deployment.ProductName}' if this deployment created it",
            Resource = server,
            Action = async context =>
            {
                using var client = await CreateClientAsync(deployment, context).ConfigureAwait(false);
                await CreateProvisioner(client, context).DestroyAsync(deployment, context.CancellationToken).ConfigureAwait(false);
            },
        };
        destroy.DependsOn(WellKnownPipelineSteps.DestroyPrereq);
        destroy.RequiredBy(WellKnownPipelineSteps.Destroy);

        return [provision, databases, configure, destroy];
    }

    /// <summary>
    /// Orders the steps against the ones Aspire and the compute environments add.
    /// </summary>
    public static void Configure(RavenDBCloudDeployment deployment, PipelineConfigurationContext context)
    {
        var server = deployment.Server;
        var provision = context.Steps.FirstOrDefault(s => s.Name == ProvisionStepName(server));
        var configure = context.Steps.FirstOrDefault(s => s.Name == ConfigureStepName(server));
        var destroy = context.Steps.FirstOrDefault(s => s.Name == DestroyStepName(server));

        // The API key is a parameter; it is resolved (or prompted for) by process-parameters.
        if (context.Steps.Any(s => s.Name == WellKnownPipelineSteps.ProcessParameters))
        {
            provision?.DependsOn(WellKnownPipelineSteps.ProcessParameters);
            destroy?.DependsOn(WellKnownPipelineSteps.ProcessParameters);
        }

        foreach (var environment in context.Model.Resources.OfType<IComputeEnvironmentResource>())
        {
            // Docker Compose writes its environment files in prepare-{env} and uses them in docker-compose-up-{env}:
            // the product URL has to land in between.
            var prepareName = $"prepare-{environment.Name}";

            if (configure is not null && context.Steps.Any(s => s.Name == prepareName))
            {
                configure.DependsOn(prepareName);
            }

            context.Steps.FirstOrDefault(s => s.Name == $"docker-compose-up-{environment.Name}")?.DependsOn(ConfigureStepName(server));

            // Stop the application before the product goes away.
            var composeDownName = $"destroy-compose-{environment.Name}";

            if (destroy is not null && context.Steps.Any(s => s.Name == composeDownName))
            {
                destroy.DependsOn(composeDownName);
            }
        }

        // Azure Container Apps takes the URL as a Bicep parameter of each container app: the product must be known
        // before those are provisioned.
        foreach (var step in context.Steps)
        {
            if (step.Name.StartsWith("provision-", StringComparison.Ordinal) &&
                step.Name.EndsWith("-containerapp", StringComparison.Ordinal))
            {
                step.DependsOn(ProvisionStepName(server));
            }
        }
    }

    /// <summary>
    /// Fills the product URL into the environment files the compute environments generated.
    /// </summary>
    /// <remarks>
    /// Docker Compose's prepare step only resolves parameters and container images, so a value that is only known
    /// at deploy time stays blank in <c>.env</c>. The Bitwarden integration works around the same gap.
    /// </remarks>
    private static async Task PatchEnvironmentFilesAsync(RavenDBCloudDeployment deployment, PipelineStepContext context)
    {
        if (deployment.Endpoint.Url is not { } url)
        {
            return;
        }

        var environments = context.Model.Resources.OfType<IComputeEnvironmentResource>().ToList();

        if (environments.Count == 0)
        {
            return;
        }

        var outputService = context.Services.GetRequiredService<IPipelineOutputService>();
        var environmentName = context.Services.GetService<IHostEnvironment>()?.EnvironmentName ?? "Production";
        var key = ToEnvironmentVariableName(deployment.Endpoint.ValueExpression);

        foreach (var environment in environments)
        {
            var directory = environments.Count > 1
                ? outputService.GetOutputDirectory(environment)
                : outputService.GetOutputDirectory();

            foreach (var fileName in new[] { ".env", $".env.{environmentName}" })
            {
                var path = Path.Combine(directory, fileName);

                if (!File.Exists(path))
                {
                    continue;
                }

                var lines = await File.ReadAllLinesAsync(path, context.CancellationToken).ConfigureAwait(false);
                var changed = false;

                for (var i = 0; i < lines.Length; i++)
                {
                    var separator = lines[i].IndexOf('=', StringComparison.Ordinal);

                    if (separator > 0 && string.Equals(lines[i][..separator].Trim(), key, StringComparison.Ordinal))
                    {
                        lines[i] = $"{key}={url}";
                        changed = true;
                    }
                }

                if (changed)
                {
                    await File.WriteAllLinesAsync(path, lines, context.CancellationToken).ConfigureAwait(false);
                    context.Logger.LogInformation("Wrote {Key} to {File}.", key, path);
                }
            }
        }
    }

    /// <summary>How Docker Compose names the variable of a value expression: <c>{ravendb.url}</c> is <c>RAVENDB_URL</c>.</summary>
    internal static string ToEnvironmentVariableName(string valueExpression) =>
        valueExpression
            .Replace("{", string.Empty, StringComparison.Ordinal)
            .Replace("}", string.Empty, StringComparison.Ordinal)
            .Replace('.', '_')
            .Replace('-', '_')
            .ToUpperInvariant();

    private static async Task<RavenDBCloudApiClient> CreateClientAsync(RavenDBCloudDeployment deployment, PipelineStepContext context)
    {
        var apiKey = await deployment.ApiKey.GetValueAsync(context.CancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"RavenDB Cloud needs an API key for '{deployment.Server.Name}'. Set the '{deployment.ApiKey.Name}' parameter.");
        }

        return context.Services.GetRequiredService<IRavenDBCloudApiClientFactory>().Create(deployment.Options.ApiEndpoint, apiKey);
    }

    private static RavenDBCloudProvisioner CreateProvisioner(RavenDBCloudApiClient client, PipelineStepContext context) =>
        new(client, context.Services.GetRequiredService<IDeploymentStateManager>(), context.Logger);
}
