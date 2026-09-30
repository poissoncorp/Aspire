using System.Diagnostics;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Kubernetes;

/// <summary>
/// The steps of a server published to Kubernetes: once the chart is written, drop the bootstrap Jobs of earlier
/// configurations from it; before Helm, delete a bootstrap Job that failed, so that it runs again; while Helm installs
/// the chart, report what keeps the cluster or the bootstrap from getting ready; after Helm, wait until the bootstrap
/// Job has created the databases and the applications' certificates. The Job itself waits for the operator.
/// </summary>
internal static partial class RavenDBClusterPipelineSteps
{
    public static string PublishStepName(RavenDBServerResource server) => $"ravendb-cluster-publish-{server.Name}";

    public static string WatchStepName(RavenDBServerResource server) => $"ravendb-cluster-watch-{server.Name}";

    public static string WaitStepName(RavenDBServerResource server) => $"ravendb-cluster-wait-{server.Name}";

    public static string ResetStepName(RavenDBServerResource server) => $"ravendb-cluster-reset-{server.Name}";

    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(20);

    /// <summary>
    /// How long the watch runs next to Helm: Aspire runs <c>helm upgrade --wait</c> with Helm's default timeout of
    /// five minutes, and a watch that outlived a failed Helm would only hold the pipeline up.
    /// </summary>
    private static readonly TimeSpan s_watchTimeout = TimeSpan.FromMinutes(5.5);

    /// <summary>
    /// Helm creates the chart's objects within seconds of starting: a bootstrap Job that has not appeared by then means
    /// Helm failed before installing anything, and there is nothing left to watch.
    /// </summary>
    private static readonly TimeSpan s_jobAppearanceTimeout = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan s_pollInterval = TimeSpan.FromSeconds(10);

    public static IEnumerable<PipelineStep> Create(RavenDBClusterDeployment deployment)
    {
        var publish = new PipelineStep
        {
            Name = PublishStepName(deployment.Server),
            Description = $"Removes the bootstrap Jobs of earlier configurations of '{deployment.Server.Name}' from the chart",
            Resource = deployment.Server,
            Action = context => RemoveEarlierJobsAsync(deployment, context),
        };

        publish.RequiredBy(WellKnownPipelineSteps.Publish);
        yield return publish;

        var watch = new PipelineStep
        {
            Name = WatchStepName(deployment.Server),
            Description = $"Reports what keeps the RavenDB cluster or bootstrap of '{deployment.Server.Name}' from getting ready while Helm installs the chart",
            Resource = deployment.Server,
            Action = context => WatchAsync(deployment, context),
        };

        watch.RequiredBy(WellKnownPipelineSteps.Deploy);
        yield return watch;

        var reset = new PipelineStep
        {
            Name = ResetStepName(deployment.Server),
            Description = $"Deletes a failed bootstrap Job of '{deployment.Server.Name}', so that Helm creates it again",
            Resource = deployment.Server,
            Action = context => DeleteFailedJobAsync(deployment, context),
        };

        reset.DependsOn(WellKnownPipelineSteps.DeployPrereq);
        reset.RequiredBy(WellKnownPipelineSteps.Deploy);
        yield return reset;

        var wait = new PipelineStep
        {
            Name = WaitStepName(deployment.Server),
            Description = $"Waits until the RavenDB bootstrap of '{deployment.Server.Name}' has completed",
            Resource = deployment.Server,
            Action = context => WaitForBootstrapAsync(deployment, context),
        };

        wait.RequiredBy(WellKnownPipelineSteps.Deploy);
        yield return wait;
    }

    public static void Configure(RavenDBClusterDeployment deployment, PipelineConfigurationContext context)
    {
        var publish = context.Steps.FirstOrDefault(s => s.Name == PublishStepName(deployment.Server));
        var watch = context.Steps.FirstOrDefault(s => s.Name == WatchStepName(deployment.Server));
        var wait = context.Steps.FirstOrDefault(s => s.Name == WaitStepName(deployment.Server));
        var reset = context.Steps.FirstOrDefault(s => s.Name == ResetStepName(deployment.Server));

        foreach (var environment in context.Model.Resources.OfType<KubernetesEnvironmentResource>())
        {
            var writeChart = $"publish-{environment.Name}";
            var prepareHelm = $"prepare-{environment.Name}";
            var helmDeploy = $"helm-deploy-{environment.Name}";

            if (publish is not null && context.Steps.Any(s => s.Name == writeChart))
            {
                publish.DependsOn(writeChart);
            }

            // Next to Helm, not after it: when the cluster does not get ready, Helm only reports a timeout. The reset
            // needs the Job's name, known once the chart is written.
            if (context.Steps.Any(s => s.Name == prepareHelm))
            {
                watch?.DependsOn(prepareHelm);
                reset?.DependsOn(prepareHelm);
            }

            if (context.Steps.FirstOrDefault(s => s.Name == helmDeploy) is { } helm)
            {
                wait?.DependsOn(helm);

                if (reset is not null)
                {
                    helm.DependsOn(reset);
                }
            }
        }
    }

    /// <summary>
    /// Logs, as they happen, the states Helm's wait would only report as a timeout: the operator putting the cluster in
    /// its Error phase (with the reasons it gives) and the bootstrap Job failing an attempt (with its last output).
    /// Never fails the deployment; <see cref="WaitForBootstrapAsync"/> decides.
    /// </summary>
    private static async Task WatchAsync(RavenDBClusterDeployment deployment, PipelineStepContext context)
    {
        if (deployment.BootstrapJobName is not { } job ||
            context.Model.Resources.OfType<KubernetesEnvironmentResource>().FirstOrDefault() is not { } environment)
        {
            return;
        }

        var kubectl = await Kubectl.CreateAsync(environment, context.CancellationToken).ConfigureAwait(false);
        var start = DateTimeOffset.UtcNow;
        var reportedRestarts = 0;

        while (DateTimeOffset.UtcNow < start + s_watchTimeout)
        {
            var phase = await GetClusterPhaseAsync(kubectl, deployment, context.CancellationToken).ConfigureAwait(false);

            // "<name>:<type>=<status>;..." once the Job exists, empty before.
            var state = await kubectl.RunAsync(
                ["get", "job", job, "--ignore-not-found", "-o", "jsonpath={.metadata.name}:{range .status.conditions[*]}{.type}={.status};{end}"],
                context.CancellationToken, throwOnError: false).ConfigureAwait(false);

            if (state.Length == 0 && DateTimeOffset.UtcNow > start + s_jobAppearanceTimeout)
            {
                return;
            }

            // Helm waits for the cluster as well as for the applications the Job gives their certificates to.
            if (phase == "Running" && (state.Contains("Complete=True", StringComparison.Ordinal) || state.Contains("Failed=True", StringComparison.Ordinal)))
            {
                return;
            }

            if (phase == "Error")
            {
                await ReportClusterErrorAsync(deployment, kubectl, context).ConfigureAwait(false);
                return;
            }

            var restarts = await kubectl.RunAsync(
                ["get", "pods", "-l", $"job-name={job}", "-o", "jsonpath={range .items[*]}{.status.containerStatuses[0].restartCount}{\"\\n\"}{end}"],
                context.CancellationToken, throwOnError: false).ConfigureAwait(false);

            var count = restarts
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(r => int.TryParse(r, out var n) ? n : 0)
                .DefaultIfEmpty()
                .Max();

            if (count > reportedRestarts)
            {
                reportedRestarts = count;
                var output = await kubectl.RunAsync(["logs", $"job/{job}", "--previous", "--tail=5"], context.CancellationToken, throwOnError: false).ConfigureAwait(false);

                context.Logger.LogWarning(
                    "Attempt {Attempt} of the RavenDB bootstrap Job '{Job}' failed; it is retried. Its last output:{NewLine}{Output}",
                    count, job, Environment.NewLine, output.TrimEnd());
            }

            await Task.Delay(s_pollInterval, context.CancellationToken).ConfigureAwait(false);
        }
    }

    /// <returns>The phase the operator reports for the cluster, or an empty string before it reports one.</returns>
    private static async Task<string> GetClusterPhaseAsync(Kubectl kubectl, RavenDBClusterDeployment deployment, CancellationToken cancellationToken) =>
        (await kubectl.RunAsync(
            ["get", "ravendbcluster", deployment.ResourceName, "--ignore-not-found", "-o", "jsonpath={.status.phase}"],
            cancellationToken,
            throwOnError: false).ConfigureAwait(false)).Trim();

    private static async Task ReportClusterErrorAsync(RavenDBClusterDeployment deployment, Kubectl kubectl, PipelineStepContext context)
    {
        var problems = await kubectl.RunAsync(
            ["get", "ravendbcluster", deployment.ResourceName, "-o", "jsonpath={range .status.conditions[?(@.status==\"False\")]}  {.type}: {.message}{\"\\n\"}{end}"],
            context.CancellationToken, throwOnError: false).ConfigureAwait(false);
        var warnings = await kubectl.RunAsync(
            ["get", "events", "--field-selector", $"involvedObject.kind=RavenDBCluster,involvedObject.name={deployment.ResourceName},type=Warning",
             "--sort-by=.lastTimestamp", "-o", "jsonpath={range .items[*]}  {.reason}: {.message}{\"\\n\"}{end}"],
            context.CancellationToken, throwOnError: false).ConfigureAwait(false);

        context.Logger.LogWarning(
            "The RavenDB operator reports cluster '{Cluster}' in its Error phase, so Helm will wait for it in vain.{NewLine}{Problems}Latest warnings:{NewLine}{Warnings}",
            deployment.ResourceName,
            Environment.NewLine,
            problems,
            Environment.NewLine,
            string.Join(Environment.NewLine, warnings.TrimEnd().Split('\n').TakeLast(3)));
    }

    /// <summary>
    /// The Job is named after its configuration, and Aspire names the template file after the object and keeps the
    /// files of earlier publishes in the output directory: without this, every configuration change would leave one
    /// more Job in the chart.
    /// </summary>
    private static Task RemoveEarlierJobsAsync(RavenDBClusterDeployment deployment, PipelineStepContext context)
    {
        var environments = context.Model.Resources.OfType<IComputeEnvironmentResource>().ToList();

        if (deployment.BootstrapJobName is not { } job || environments.OfType<KubernetesEnvironmentResource>().FirstOrDefault() is not { } environment)
        {
            return Task.CompletedTask;
        }

        var output = context.Services.GetRequiredService<IPipelineOutputService>();
        var chart = RavenDBPublishing.OutputDirectory(output, environment, environments.Count);
        var templates = Path.Combine(chart, "templates", deployment.BootstrapName);

        if (!Directory.Exists(templates))
        {
            return Task.CompletedTask;
        }

        var current = $"{job[(deployment.BootstrapName.Length + 1)..]}.yaml";

        foreach (var file in Directory.EnumerateFiles(templates, "*.yaml"))
        {
            var name = Path.GetFileName(file);

            if (name != current && JobFileName().IsMatch(name))
            {
                File.Delete(file);
                context.Logger.LogInformation("Removed the bootstrap Job of an earlier configuration from the chart: {File}.", name);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The Job is named after its configuration, so a deployment that fixes what made it fail (DNS, a Secret) renders
    /// the same Job, which Helm leaves as it is. Deleted, it is created again and runs anew.
    /// </summary>
    private static async Task DeleteFailedJobAsync(RavenDBClusterDeployment deployment, PipelineStepContext context)
    {
        if (deployment.BootstrapJobName is not { } job ||
            context.Model.Resources.OfType<KubernetesEnvironmentResource>().FirstOrDefault() is not { } environment)
        {
            return;
        }

        var kubectl = await Kubectl.CreateAsync(environment, context.CancellationToken).ConfigureAwait(false);
        var conditions = await kubectl.RunAsync(
            ["get", "job", job, "--ignore-not-found", "-o", "jsonpath={range .status.conditions[*]}{.type}={.status};{end}"],
            context.CancellationToken, throwOnError: false).ConfigureAwait(false);

        if (conditions.Contains("Failed=True", StringComparison.Ordinal))
        {
            await kubectl.RunAsync(["delete", "job", job, "--wait=true"], context.CancellationToken).ConfigureAwait(false);
            context.Logger.LogInformation("Deleted the failed RavenDB bootstrap Job '{Job}', so that this deployment runs it again.", job);
        }
    }

    [GeneratedRegex("^[0-9a-f]{10}\\.yaml$")]
    private static partial Regex JobFileName();

    private static async Task WaitForBootstrapAsync(RavenDBClusterDeployment deployment, PipelineStepContext context)
    {
        if (deployment.BootstrapJobName is not { } job)
        {
            context.Logger.LogWarning("The RavenDB bootstrap of '{Server}' is not part of this deployment.", deployment.Server.Name);
            return;
        }

        var environment = context.Model.Resources.OfType<KubernetesEnvironmentResource>().First();
        var kubectl = await Kubectl.CreateAsync(environment, context.CancellationToken).ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow + s_timeout;
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
                throw new TimeoutException($"The RavenDB bootstrap Job '{job}' did not complete within {s_timeout}.");
            }

            if (DateTimeOffset.UtcNow - lastReport > TimeSpan.FromMinutes(1))
            {
                lastReport = DateTimeOffset.UtcNow;

                var phase = await GetClusterPhaseAsync(kubectl, deployment, context.CancellationToken).ConfigureAwait(false);

                context.Logger.LogInformation(
                    "Waiting for the RavenDB bootstrap Job '{Job}'{Phase}.",
                    job,
                    phase.Length == 0 ? string.Empty : $" (cluster: {phase})");
            }

            await Task.Delay(s_pollInterval, context.CancellationToken).ConfigureAwait(false);
        }

        var url = await deployment.Url.GetValueAsync(context.CancellationToken).ConfigureAwait(false);
        context.Summary.Add($"RavenDB ({deployment.Server.Name})", url ?? string.Empty);
    }

    /// <summary>kubectl, pointed at the namespace and kubeconfig Helm deploys with.</summary>
    private sealed class Kubectl(IReadOnlyList<string> globalArguments)
    {
        public static async Task<Kubectl> CreateAsync(KubernetesEnvironmentResource environment, CancellationToken cancellationToken)
        {
            // One stalled call must not outlive the step's own deadlines.
            var arguments = new List<string> { "--request-timeout=30s" };

            if (environment.KubeConfigPath is { Length: > 0 } kubeConfig)
            {
                arguments.AddRange(["--kubeconfig", kubeConfig]);
            }

            // Aspire deploys to the "default" namespace when none is set, whatever the kube context says.
            var ns = environment.Annotations.OfType<KubernetesNamespaceAnnotation>().LastOrDefault() is { } annotation
                ? await annotation.Namespace.GetValueAsync(cancellationToken).ConfigureAwait(false)
                : null;

            arguments.AddRange(["--namespace", ns is { Length: > 0 } ? ns : "default"]);

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

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            if (process.ExitCode != 0 && throwOnError)
            {
                throw new InvalidOperationException($"kubectl {string.Join(' ', arguments)} failed: {(await error.ConfigureAwait(false)).Trim()}");
            }

            return await output.ConfigureAwait(false);
        }
    }
}
