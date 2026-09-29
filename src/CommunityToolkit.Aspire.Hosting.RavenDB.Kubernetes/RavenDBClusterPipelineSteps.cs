using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Kubernetes;

/// <summary>
/// The deploy step of a server published to Kubernetes: after Helm has installed the chart, wait until the bootstrap
/// Job has created the databases and the applications' certificates. The Job itself waits for the operator.
/// </summary>
internal static class RavenDBClusterPipelineSteps
{
    public static string WaitStepName(RavenDBServerResource server) => $"ravendb-cluster-wait-{server.Name}";

    public static TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(20);

    public static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    public static PipelineStep Create(RavenDBClusterDeployment deployment)
    {
        var step = new PipelineStep
        {
            Name = WaitStepName(deployment.Server),
            Description = $"Waits until the RavenDB bootstrap of '{deployment.Server.Name}' has completed",
            Resource = deployment.Server,
            Action = context => WaitForBootstrapAsync(deployment, context),
        };

        step.RequiredBy(WellKnownPipelineSteps.Deploy);
        return step;
    }

    public static void Configure(RavenDBClusterDeployment deployment, PipelineConfigurationContext context)
    {
        var wait = context.Steps.FirstOrDefault(s => s.Name == WaitStepName(deployment.Server));

        foreach (var environment in context.Model.Resources.OfType<KubernetesEnvironmentResource>())
        {
            var helmDeploy = $"helm-deploy-{environment.Name}";

            if (wait is not null && context.Steps.Any(s => s.Name == helmDeploy))
            {
                wait.DependsOn(helmDeploy);
            }
        }
    }

    private static async Task WaitForBootstrapAsync(RavenDBClusterDeployment deployment, PipelineStepContext context)
    {
        if (deployment.BootstrapJobName is not { } job)
        {
            context.Logger.LogWarning("The RavenDB bootstrap of '{Server}' is not part of this deployment.", deployment.Server.Name);
            return;
        }

        var environment = context.Model.Resources.OfType<KubernetesEnvironmentResource>().First();
        var kubectl = await Kubectl.CreateAsync(environment, context.CancellationToken).ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow + Timeout;
        var lastReport = DateTimeOffset.MinValue;

        while (true)
        {
            var conditions = await kubectl.RunAsync(
                ["get", "job", job, "-o", "jsonpath={range .status.conditions[*]}{.type}={.status};{end}"],
                context.CancellationToken).ConfigureAwait(false);

            if (conditions.Contains("Complete=True", StringComparison.Ordinal))
            {
                context.Logger.LogInformation("The RavenDB bootstrap of '{Server}' has completed.", deployment.Server.Name);
                break;
            }

            if (conditions.Contains("Failed=True", StringComparison.Ordinal))
            {
                var logs = await kubectl.RunAsync(["logs", $"job/{job}", "--tail=20"], context.CancellationToken, throwOnError: false).ConfigureAwait(false);

                throw new InvalidOperationException(
                    $"The RavenDB bootstrap Job '{job}' failed. Its last output:{Environment.NewLine}{logs}");
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"The RavenDB bootstrap Job '{job}' did not complete within {Timeout}.");
            }

            if (DateTimeOffset.UtcNow - lastReport > TimeSpan.FromMinutes(1))
            {
                lastReport = DateTimeOffset.UtcNow;

                var phase = deployment.IsExisting
                    ? null
                    : await kubectl.RunAsync(["get", "ravendbcluster", deployment.ResourceName, "-o", "jsonpath={.status.phase}"], context.CancellationToken, throwOnError: false).ConfigureAwait(false);

                context.Logger.LogInformation(
                    "Waiting for the RavenDB bootstrap Job '{Job}'{Phase}.",
                    job,
                    string.IsNullOrWhiteSpace(phase) ? string.Empty : $" (cluster: {phase.Trim()})");
            }

            await Task.Delay(PollInterval, context.CancellationToken).ConfigureAwait(false);
        }

        var url = await deployment.Url.GetValueAsync(context.CancellationToken).ConfigureAwait(false);
        context.Summary.Add($"RavenDB ({deployment.Server.Name})", url ?? string.Empty);
    }

    /// <summary>kubectl, pointed at the namespace and kubeconfig Helm deploys with.</summary>
    private sealed class Kubectl(IReadOnlyList<string> globalArguments)
    {
        public static async Task<Kubectl> CreateAsync(KubernetesEnvironmentResource environment, CancellationToken cancellationToken)
        {
            var arguments = new List<string>();

            if (environment.KubeConfigPath is { Length: > 0 } kubeConfig)
            {
                arguments.AddRange(["--kubeconfig", kubeConfig]);
            }

            if (environment.Annotations.OfType<KubernetesNamespaceAnnotation>().LastOrDefault() is { } annotation &&
                await annotation.Namespace.GetValueAsync(cancellationToken).ConfigureAwait(false) is { Length: > 0 } ns)
            {
                arguments.AddRange(["--namespace", ns]);
            }

            return new Kubectl(arguments);
        }

        public async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, bool throwOnError = true)
        {
            var start = new ProcessStartInfo("kubectl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var argument in globalArguments.Concat(arguments))
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("kubectl could not be started.");

            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0 && throwOnError)
            {
                throw new InvalidOperationException($"kubectl {string.Join(' ', arguments)} failed: {(await error.ConfigureAwait(false)).Trim()}");
            }

            return await output.ConfigureAwait(false);
        }
    }
}
