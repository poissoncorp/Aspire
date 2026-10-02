using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Kubernetes;

/// <summary>How long the steps wait and how often they look; shortened in tests.</summary>
/// <param name="Wait">How long the deployment waits for the bootstrap Job after Helm.</param>
/// <param name="Watch">
/// How long the watch runs next to Helm: Aspire runs <c>helm upgrade --wait</c> with Helm's default timeout of five
/// minutes, and a watch that outlived a failed Helm would only hold the pipeline up.
/// </param>
/// <param name="JobAppearance">
/// Helm creates the chart's objects within seconds of starting: a bootstrap Job that has not appeared by then means
/// Helm failed before installing anything, and there is nothing left to watch.
/// </param>
/// <param name="Poll">Delay between two looks at the cluster.</param>
internal sealed record RavenDBClusterTimings(TimeSpan Wait, TimeSpan Watch, TimeSpan JobAppearance, TimeSpan Poll)
{
    public static RavenDBClusterTimings Default { get; } =
        new(TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(5.5), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(10));
}

/// <summary>
/// The steps of a server published to Kubernetes: around the writing of the chart, drop the bootstrap's files of
/// earlier publishes that this one does not write again; before Helm, check that the operator (and Traefik's resource types, for Traefik) are
/// installed and delete a bootstrap Job that failed, so that it runs again; while Helm installs the chart, report
/// what keeps the cluster or the bootstrap from getting ready; after Helm, wait until the bootstrap Job has created
/// the databases and the applications' certificates. The Job itself waits for the operator.
/// </summary>
internal static class RavenDBClusterPipelineSteps
{
    public static string SnapshotStepName(RavenDBServerResource server) => $"ravendb-cluster-snapshot-{server.Name}";

    public static string PublishStepName(RavenDBServerResource server) => $"ravendb-cluster-publish-{server.Name}";

    public static string WatchStepName(RavenDBServerResource server) => $"ravendb-cluster-watch-{server.Name}";

    public static string WaitStepName(RavenDBServerResource server) => $"ravendb-cluster-wait-{server.Name}";

    public static string ResetStepName(RavenDBServerResource server) => $"ravendb-cluster-reset-{server.Name}";

    public static string CheckStepName(RavenDBServerResource server) => $"ravendb-cluster-check-{server.Name}";

    /// <summary>The operator's cluster type, fully qualified so that no other operator's RavenDBCluster answers.</summary>
    internal const string ClusterResourceType = "ravendbclusters.ravendb.ravendb.io";

    /// <summary>The Traefik type of the routes the chart adds for Traefik.</summary>
    internal const string TraefikRouteResourceType = "ingressroutetcps.traefik.io";

    public static IEnumerable<PipelineStep> Create(RavenDBClusterDeployment deployment)
    {
        var snapshot = new PipelineStep
        {
            Name = SnapshotStepName(deployment.Server),
            Description = $"Notes the chart files of earlier publishes of '{deployment.Server.Name}', before the chart is written again",
            Resource = deployment.Server,
            Action = context => NoteEarlierChartFilesAsync(deployment, context),
        };

        snapshot.RequiredBy(WellKnownPipelineSteps.Publish);
        yield return snapshot;

        var publish = new PipelineStep
        {
            Name = PublishStepName(deployment.Server),
            Description = $"Removes the chart files of earlier publishes of '{deployment.Server.Name}' that this one did not write again",
            Resource = deployment.Server,
            Action = context => RemoveLeftoversAsync(deployment, context),
        };

        publish.RequiredBy(WellKnownPipelineSteps.Publish);
        yield return publish;

        var watch = new PipelineStep
        {
            Name = WatchStepName(deployment.Server),
            Description = $"Reports what keeps the RavenDB cluster or bootstrap of '{deployment.Server.Name}' from getting ready while Helm installs the chart",
            Resource = deployment.Server,
            Action = context => WithKubectlAsync(context, kubectl => WatchAsync(kubectl, deployment, context.Logger, RavenDBClusterTimings.Default, context.CancellationToken)),
        };

        // Only once Helm is going to run: without the operator there would be nothing to watch.
        watch.DependsOn(CheckStepName(deployment.Server));
        watch.RequiredBy(WellKnownPipelineSteps.Deploy);
        yield return watch;

        var check = new PipelineStep
        {
            Name = CheckStepName(deployment.Server),
            Description = $"Checks that the RavenDB operator is installed in the Kubernetes cluster of '{deployment.Server.Name}'",
            Resource = deployment.Server,
            Action = context => WithKubectlAsync(context, kubectl => EnsurePrerequisitesAsync(kubectl, deployment, context.CancellationToken)),
        };

        check.DependsOn(WellKnownPipelineSteps.DeployPrereq);
        check.RequiredBy(WellKnownPipelineSteps.Deploy);
        yield return check;

        var reset = new PipelineStep
        {
            Name = ResetStepName(deployment.Server),
            Description = $"Deletes a failed bootstrap Job of '{deployment.Server.Name}', so that Helm creates it again",
            Resource = deployment.Server,
            Action = context => WithKubectlAsync(context, kubectl => DeleteFailedJobAsync(kubectl, deployment, context.Logger, context.CancellationToken)),
        };

        reset.DependsOn(WellKnownPipelineSteps.DeployPrereq);
        reset.RequiredBy(WellKnownPipelineSteps.Deploy);
        yield return reset;

        var wait = new PipelineStep
        {
            Name = WaitStepName(deployment.Server),
            Description = $"Waits until the RavenDB bootstrap of '{deployment.Server.Name}' has completed",
            Resource = deployment.Server,
            Action = async context =>
            {
                await WithKubectlAsync(context, kubectl => WaitForBootstrapAsync(kubectl, deployment, context.Logger, RavenDBClusterTimings.Default, context.CancellationToken)).ConfigureAwait(false);

                var url = await deployment.Url.GetValueAsync(context.CancellationToken).ConfigureAwait(false);
                context.Summary.Add($"RavenDB ({deployment.Server.Name})", url ?? string.Empty);
            },
        };

        wait.RequiredBy(WellKnownPipelineSteps.Deploy);
        yield return wait;
    }

    public static void Configure(RavenDBClusterDeployment deployment, PipelineConfigurationContext context)
    {
        var snapshot = context.Steps.FirstOrDefault(s => s.Name == SnapshotStepName(deployment.Server));
        var publish = context.Steps.FirstOrDefault(s => s.Name == PublishStepName(deployment.Server));
        var watch = context.Steps.FirstOrDefault(s => s.Name == WatchStepName(deployment.Server));
        var wait = context.Steps.FirstOrDefault(s => s.Name == WaitStepName(deployment.Server));
        var reset = context.Steps.FirstOrDefault(s => s.Name == ResetStepName(deployment.Server));
        var check = context.Steps.FirstOrDefault(s => s.Name == CheckStepName(deployment.Server));

        foreach (var environment in context.Model.Resources.OfType<KubernetesEnvironmentResource>())
        {
            var writeChart = AspireSteps.Required(context, AspireSteps.Publish(environment));
            publish?.DependsOn(writeChart);

            if (snapshot is not null)
            {
                writeChart.DependsOn(snapshot);
            }

            // Next to Helm, not after it: when the cluster does not get ready, Helm only reports a timeout. The reset
            // needs the Job's name, known once the chart is written.
            var prepare = AspireSteps.Required(context, AspireSteps.Prepare(environment));
            watch?.DependsOn(prepare);
            reset?.DependsOn(prepare);

            var helm = AspireSteps.Required(context, AspireSteps.HelmDeploy(environment));
            wait?.DependsOn(helm);

            if (reset is not null)
            {
                helm.DependsOn(reset);
            }

            if (check is not null)
            {
                helm.DependsOn(check);
            }
        }
    }

    private static async Task WithKubectlAsync(PipelineStepContext context, Func<IKubectl, Task> action)
    {
        if (context.Model.Resources.OfType<KubernetesEnvironmentResource>().FirstOrDefault() is not { } environment)
        {
            return;
        }

        await action(await Kubectl.CreateAsync(environment, context.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
    }

    /// <summary>
    /// Without the operator, Helm fails on the chart's <c>RavenDBCluster</c>, and without Traefik's resource types on
    /// its routes, with messages that say neither what is missing nor how to install it. Any other failure to ask is
    /// left to Helm, which reports it itself.
    /// </summary>
    internal static async Task EnsurePrerequisitesAsync(IKubectl kubectl, RavenDBClusterDeployment deployment, CancellationToken cancellationToken)
    {
        if (IsUnknownResourceType(await kubectl.TryRunAsync(["get", ClusterResourceType, "--output=name"], cancellationToken).ConfigureAwait(false)))
        {
            throw new InvalidOperationException(
                "The RavenDB operator is not installed in the Kubernetes cluster: it has no RavenDBCluster resource type. " +
                "Install cert-manager and the operator once per cluster, as the Prerequisites of " +
                "https://www.nuget.org/packages/CommunityToolkit.Aspire.Hosting.RavenDB.Kubernetes show.");
        }

        if (deployment.Options.IngressController == RavenDBIngressController.Traefik &&
            IsUnknownResourceType(await kubectl.TryRunAsync(["get", TraefikRouteResourceType, "--output=name"], cancellationToken).ConfigureAwait(false)))
        {
            throw new InvalidOperationException(
                "Traefik's resource types are not installed in the Kubernetes cluster: it has no IngressRouteTCP, which " +
                "the chart routes the RavenDB nodes with. Install Traefik with its Kubernetes CRD provider, or set " +
                "IngressController to the ingress controller the cluster runs.");
        }
    }

    private static bool IsUnknownResourceType(KubectlResult result) =>
        result.ExitCode != 0 && IsUnknownResourceType(result.Error);

    internal static bool IsUnknownResourceType(string kubectlError) =>
        kubectlError.Contains("the server doesn't have a resource type", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Logs, as they happen, the states Helm's wait would only report as a timeout: the operator putting the cluster in
    /// its Error phase (with the reasons it gives) and the bootstrap Job failing an attempt (with its last output).
    /// Never fails the deployment; <see cref="WaitForBootstrapAsync"/> decides.
    /// </summary>
    internal static async Task WatchAsync(IKubectl kubectl, RavenDBClusterDeployment deployment, ILogger logger, RavenDBClusterTimings timings, CancellationToken cancellationToken)
    {
        if (deployment.BootstrapJobName is not { } job)
        {
            return;
        }

        try
        {
            var start = DateTimeOffset.UtcNow;
            var reportedRestarts = 0;

            while (DateTimeOffset.UtcNow < start + timings.Watch)
            {
                var phase = await GetClusterPhaseAsync(kubectl, deployment, cancellationToken).ConfigureAwait(false);

                // "<name>:<type>=<status>;..." once the Job exists, empty before.
                var state = await kubectl.OutputAsync(
                    ["get", "job", job, "--ignore-not-found", "-o", "jsonpath={.metadata.name}:{range .status.conditions[*]}{.type}={.status};{end}"],
                    cancellationToken).ConfigureAwait(false);

                if (state.Length == 0 && DateTimeOffset.UtcNow > start + timings.JobAppearance)
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
                    await ReportClusterErrorAsync(kubectl, deployment, logger, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var restarts = await kubectl.OutputAsync(
                    ["get", "pods", "-l", $"job-name={job}", "-o", "jsonpath={range .items[*]}{.status.containerStatuses[0].restartCount}{\"\\n\"}{end}"],
                    cancellationToken).ConfigureAwait(false);

                var count = restarts
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(r => int.TryParse(r, out var n) ? n : 0)
                    .DefaultIfEmpty()
                    .Max();

                if (count > reportedRestarts)
                {
                    reportedRestarts = count;
                    var output = await kubectl.OutputAsync(["logs", $"job/{job}", "--previous", "--tail=5"], cancellationToken).ConfigureAwait(false);

                    logger.LogWarning(
                        "Attempt {Attempt} of the RavenDB bootstrap Job '{Job}' failed; it is retried. Its last output:{NewLine}{Output}",
                        count, job, Environment.NewLine, output.TrimEnd());
                }

                await Task.Delay(timings.Poll, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Watching the RavenDB cluster '{Cluster}' stopped; Helm still decides.", deployment.ResourceName);
        }
    }

    /// <returns>The phase the operator reports for the cluster, or an empty string before it reports one.</returns>
    private static async Task<string> GetClusterPhaseAsync(IKubectl kubectl, RavenDBClusterDeployment deployment, CancellationToken cancellationToken) =>
        (await kubectl.OutputAsync(
            ["get", ClusterResourceType, deployment.ResourceName, "--ignore-not-found", "-o", "jsonpath={.status.phase}"],
            cancellationToken).ConfigureAwait(false)).Trim();

    private static async Task ReportClusterErrorAsync(IKubectl kubectl, RavenDBClusterDeployment deployment, ILogger logger, CancellationToken cancellationToken)
    {
        var problems = await kubectl.OutputAsync(
            ["get", ClusterResourceType, deployment.ResourceName, "-o", "jsonpath={range .status.conditions[?(@.status==\"False\")]}  {.type}: {.message}{\"\\n\"}{end}"],
            cancellationToken).ConfigureAwait(false);
        var warnings = await kubectl.OutputAsync(
            ["get", "events", "--field-selector", $"involvedObject.kind=RavenDBCluster,involvedObject.name={deployment.ResourceName},type=Warning",
             "--sort-by=.lastTimestamp", "-o", "jsonpath={range .items[*]}  {.reason}: {.message}{\"\\n\"}{end}"],
            cancellationToken).ConfigureAwait(false);

        logger.LogWarning(
            "The RavenDB operator reports cluster '{Cluster}' in its Error phase, so Helm will wait for it in vain.{NewLine}{Problems}Latest warnings:{NewLine}{Warnings}",
            deployment.ResourceName,
            Environment.NewLine,
            problems,
            Environment.NewLine,
            string.Join(Environment.NewLine, warnings.TrimEnd().Split('\n').TakeLast(3)));
    }

    /// <summary>
    /// Aspire writes the chart into the same directory on every publish and never deletes what earlier ones wrote. The
    /// bootstrap's own files change with its configuration (the Job is named after it, the Traefik routes and the
    /// applications' Secrets come and go), so the ones this publish does not write again are leftovers.
    /// </summary>
    private static Task NoteEarlierChartFilesAsync(RavenDBClusterDeployment deployment, PipelineStepContext context)
    {
        var directory = BootstrapChartDirectory(deployment, context);

        deployment.EarlierChartFiles = directory is not null && Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory).ToDictionary(f => f, File.GetLastWriteTimeUtc)
            : [];

        return Task.CompletedTask;
    }

    private static Task RemoveLeftoversAsync(RavenDBClusterDeployment deployment, PipelineStepContext context)
    {
        foreach (var (file, written) in deployment.EarlierChartFiles ?? [])
        {
            if (File.Exists(file) && File.GetLastWriteTimeUtc(file) == written)
            {
                File.Delete(file);
                context.Logger.LogInformation("Removed a chart file of an earlier configuration: {File}.", Path.GetFileName(file));
            }
        }

        return Task.CompletedTask;
    }

    private static string? BootstrapChartDirectory(RavenDBClusterDeployment deployment, PipelineStepContext context)
    {
        var environments = context.Model.Resources.OfType<IComputeEnvironmentResource>().ToList();

        if (environments.OfType<KubernetesEnvironmentResource>().FirstOrDefault() is not { } environment)
        {
            return null;
        }

        var output = context.Services.GetRequiredService<IPipelineOutputService>();
        return Path.Combine(RavenDBPublishing.OutputDirectory(output, environment, environments.Count), "templates", deployment.BootstrapName);
    }

    /// <summary>
    /// The Job is named after its configuration, so a deployment that fixes what made it fail (DNS, a Secret) renders
    /// the same Job, which Helm leaves as it is. Deleted, it is created again and runs anew.
    /// </summary>
    internal static async Task DeleteFailedJobAsync(IKubectl kubectl, RavenDBClusterDeployment deployment, ILogger logger, CancellationToken cancellationToken)
    {
        if (deployment.BootstrapJobName is not { } job)
        {
            return;
        }

        var conditions = await kubectl.OutputAsync(
            ["get", "job", job, "--ignore-not-found", "-o", "jsonpath={range .status.conditions[*]}{.type}={.status};{end}"],
            cancellationToken).ConfigureAwait(false);

        if (conditions.Contains("Failed=True", StringComparison.Ordinal))
        {
            await kubectl.RunAsync(["delete", "job", job, "--wait=true"], cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Deleted the failed RavenDB bootstrap Job '{Job}', so that this deployment runs it again.", job);
        }
    }

    /// <summary>
    /// Waits for the bootstrap Job. A failed look at it (the API server busy, a dropped connection) is looked at again
    /// until the deadline: Helm has already succeeded, so giving up on one error would fail a deployment that works.
    /// </summary>
    internal static async Task WaitForBootstrapAsync(IKubectl kubectl, RavenDBClusterDeployment deployment, ILogger logger, RavenDBClusterTimings timings, CancellationToken cancellationToken)
    {
        if (deployment.BootstrapJobName is not { } job)
        {
            logger.LogWarning("The RavenDB bootstrap of '{Server}' is not part of this deployment.", deployment.Server.Name);
            return;
        }

        var deadline = DateTimeOffset.UtcNow + timings.Wait;
        var lastReport = DateTimeOffset.MinValue;
        var lastError = string.Empty;

        while (true)
        {
            var result = await kubectl.TryRunAsync(
                ["get", "job", job, "-o", "jsonpath={range .status.conditions[*]}{.type}={.status};{end}"],
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                lastError = result.Error.Trim();
            }
            else if (result.Output.Contains("Complete=True", StringComparison.Ordinal))
            {
                logger.LogInformation("The RavenDB bootstrap of '{Server}' has completed.", deployment.Server.Name);
                return;
            }
            else if (result.Output.Contains("Failed=True", StringComparison.Ordinal))
            {
                var logs = await kubectl.OutputAsync(["logs", $"job/{job}", "--tail=20"], cancellationToken).ConfigureAwait(false);

                throw new InvalidOperationException(
                    $"The RavenDB bootstrap Job '{job}' failed. Its last output:{Environment.NewLine}{logs}");
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"The RavenDB bootstrap Job '{job}' did not complete within {timings.Wait}." +
                    (lastError.Length > 0 ? $" Last error: {lastError}" : string.Empty));
            }

            if (DateTimeOffset.UtcNow - lastReport > TimeSpan.FromMinutes(1))
            {
                lastReport = DateTimeOffset.UtcNow;

                var phase = await GetClusterPhaseAsync(kubectl, deployment, cancellationToken).ConfigureAwait(false);

                logger.LogInformation(
                    "Waiting for the RavenDB bootstrap Job '{Job}'{Phase}.",
                    job,
                    phase.Length == 0 ? string.Empty : $" (cluster: {phase})");
            }

            await Task.Delay(timings.Poll, cancellationToken).ConfigureAwait(false);
        }
    }
}
