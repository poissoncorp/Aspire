#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental

using Aspire.Hosting;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Kubernetes.Tests;

/// <summary>What the deploy steps do with what kubectl answers, without a cluster.</summary>
public class RavenDBClusterPipelineStepTests
{
    private const string Job = "ravendb-bootstrap-0123456789";

    private static readonly RavenDBClusterTimings s_fast = new(
        Wait: TimeSpan.FromMilliseconds(300),
        Watch: TimeSpan.FromMilliseconds(300),
        JobAppearance: TimeSpan.FromMilliseconds(100),
        Poll: TimeSpan.FromMilliseconds(1));

    private readonly ListLogger _logger = new();

    [Fact]
    public async Task CheckStopsWithoutTheOperator()
    {
        var kubectl = new FakeKubectl().On("get ravendbclusters", Unknown("ravendbclusters.ravendb.ravendb.io"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => RavenDBClusterPipelineSteps.EnsurePrerequisitesAsync(kubectl, Deployment(), default));

        Assert.Contains("The RavenDB operator is not installed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckStopsWithoutTraefiksResourceTypesWhenTheChartRoutesThroughTraefik()
    {
        var kubectl = new FakeKubectl()
            .On("get ravendbclusters", Ok())
            .On("get ingressroutetcps", Unknown("ingressroutetcps.traefik.io"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => RavenDBClusterPipelineSteps.EnsurePrerequisitesAsync(kubectl, Deployment(), default));

        Assert.Contains("Traefik's resource types are not installed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsksForTraefikOnlyWhenTheChartRoutesThroughIt()
    {
        var kubectl = new FakeKubectl().On("get ravendbclusters", Ok());

        await RavenDBClusterPipelineSteps.EnsurePrerequisitesAsync(kubectl, Deployment(RavenDBIngressController.Nginx), default);

        Assert.DoesNotContain(kubectl.Calls, c => c.Contains("ingressroutetcps", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckLeavesOtherFailuresToHelm()
    {
        var kubectl = new FakeKubectl().On("get", Fail("Error from server (Forbidden): ravendbclusters.ravendb.ravendb.io is forbidden"));

        await RavenDBClusterPipelineSteps.EnsurePrerequisitesAsync(kubectl, Deployment(), default);
    }

    [Theory]
    [InlineData("error: the server doesn't have a resource type \"ravendbclusters\"", true)]
    [InlineData("Error from server (Forbidden): ravendbclusters.ravendb.ravendb.io is forbidden", false)]
    [InlineData("Unable to connect to the server: dial tcp 127.0.0.1:6443: connect: connection refused", false)]
    public void OnlyAMissingResourceTypeMeansSomethingIsNotInstalled(string kubectlError, bool missing) =>
        Assert.Equal(missing, RavenDBClusterPipelineSteps.IsUnknownResourceType(kubectlError));

    [Fact]
    public async Task WatchReportsTheOperatorsErrorPhaseWithItsReasons()
    {
        var kubectl = new FakeKubectl()
            .On("jsonpath={.status.phase}", Ok("Error"))
            .On("get job", Ok($"{Job}:"))
            .On("status==", Ok("  BootstrapCompleted: bootstrap job failed\n"))
            .On("get events", Ok("  BootstrapFailed: Condition BootstrapCompleted changed to False\n"));

        await RavenDBClusterPipelineSteps.WatchAsync(kubectl, Deployment(), _logger, s_fast, default);

        var warning = Assert.Single(_logger.Warnings);
        Assert.Contains("in its Error phase", warning, StringComparison.Ordinal);
        Assert.Contains("BootstrapCompleted: bootstrap job failed", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WatchReportsAFailedBootstrapAttemptWithItsOutput()
    {
        var kubectl = new FakeKubectl()
            .On("jsonpath={.status.phase}", Ok(""), Ok("Running"))
            .On("get job", Ok($"{Job}:"), Ok($"{Job}:Complete=True;"))
            .On("get pods", Ok("1\n"))
            .On("logs", Ok("Creating database 'orders' failed: HTTP 500\n"));

        await RavenDBClusterPipelineSteps.WatchAsync(kubectl, Deployment(), _logger, s_fast, default);

        var warning = Assert.Single(_logger.Warnings);
        Assert.Contains($"Attempt 1 of the RavenDB bootstrap Job '{Job}' failed", warning, StringComparison.Ordinal);
        Assert.Contains("Creating database 'orders' failed: HTTP 500", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WatchNeverFailsTheDeployment()
    {
        var kubectl = new FakeKubectl().Throw(new InvalidOperationException("kubectl was not found."));

        await RavenDBClusterPipelineSteps.WatchAsync(kubectl, Deployment(), _logger, s_fast, default);

        Assert.Contains("Watching the RavenDB cluster 'ravendb' stopped", Assert.Single(_logger.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitEndsWhenTheJobCompletes()
    {
        var kubectl = new FakeKubectl().On("get job", Ok("Complete=True;"));

        await RavenDBClusterPipelineSteps.WaitForBootstrapAsync(kubectl, Deployment(), _logger, s_fast, default);
    }

    [Fact]
    public async Task WaitLooksAgainAfterAFailedLook()
    {
        var kubectl = new FakeKubectl()
            .On("get job", Fail("Unable to connect to the server: connection refused"), Ok("Complete=True;"))
            .On("jsonpath={.status.phase}", Ok("Running"));

        await RavenDBClusterPipelineSteps.WaitForBootstrapAsync(kubectl, Deployment(), _logger, s_fast, default);
    }

    [Fact]
    public async Task WaitFailsWithTheOutputOfAFailedJob()
    {
        var kubectl = new FakeKubectl()
            .On("get job", Ok("Failed=True;"))
            .On("logs", Ok("Creating database 'orders' failed: HTTP 500\n"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => RavenDBClusterPipelineSteps.WaitForBootstrapAsync(kubectl, Deployment(), _logger, s_fast, default));

        Assert.Contains("Creating database 'orders' failed: HTTP 500", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitGivesUpWithTheLastError()
    {
        var kubectl = new FakeKubectl()
            .On("get job", Fail("error: You must be logged in to the server (Unauthorized)"))
            .On("jsonpath={.status.phase}", Ok(""));

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => RavenDBClusterPipelineSteps.WaitForBootstrapAsync(kubectl, Deployment(), _logger, s_fast, default));

        Assert.Contains("Last error: error: You must be logged in to the server (Unauthorized)", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Failed=True;", true)]
    [InlineData("Complete=True;", false)]
    [InlineData("", false)]
    public async Task ResetDeletesOnlyAFailedJob(string conditions, bool deleted)
    {
        var kubectl = new FakeKubectl()
            .On("delete job", Ok())
            .On("get job", Ok(conditions));

        await RavenDBClusterPipelineSteps.DeleteFailedJobAsync(kubectl, Deployment(), _logger, default);

        Assert.Equal(deleted, kubectl.Calls.Any(c => c.StartsWith("delete job", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task StepsAreOrderedAgainstAspiresOwnSteps()
    {
        var directory = Directory.CreateTempSubdirectory(".ravendb-cluster-steps-test");

        try
        {
            using var builder = TestDistributedApplicationBuilder.Create(
                "AppHost:Operation=publish", $"Pipeline:OutputPath={directory.FullName}", "Pipeline:Step=publish");
            builder.Configuration["Parameters:ravendb-license"] = "{\"Id\":\"x\"}";
            builder.AddKubernetesEnvironment("k8s");

            builder.AddRavenDB("ravendb")
                .WithLicense(builder.AddParameter("ravendb-license", secret: true))
                .PublishAsRavenDBCluster(cluster =>
                {
                    cluster.Domain = "ravendb.example.test";
                    cluster.Image = "ravendb/ravendb:7.2.6-ubuntu.24.04-x64";
                    cluster.WithCertificates("ravendb-server", "ravendb-admin");
                });

            // Registered after the cluster's: sees the steps as the cluster left them.
            var steps = new Dictionary<string, IReadOnlyList<string>>();
            builder.AddParameter("probe").WithPipelineConfiguration(context =>
            {
                foreach (var step in context.Steps)
                {
                    steps[step.Name] = [.. step.DependsOnSteps];
                }
            });

            using var app = builder.Build();
            await app.RunAsync(TestContext.Current.CancellationToken);

            // Aspire's real steps, so a rename in Aspire fails here instead of leaving the steps unordered.
            Assert.Contains("ravendb-cluster-check-ravendb", steps["helm-deploy-k8s"]);
            Assert.Contains("ravendb-cluster-reset-ravendb", steps["helm-deploy-k8s"]);
            Assert.Contains("helm-deploy-k8s", steps["ravendb-cluster-wait-ravendb"]);
            Assert.Contains("prepare-k8s", steps["ravendb-cluster-watch-ravendb"]);
            Assert.Contains("ravendb-cluster-check-ravendb", steps["ravendb-cluster-watch-ravendb"]);
            Assert.Contains("publish-k8s", steps["ravendb-cluster-publish-ravendb"]);
            Assert.Contains("ravendb-cluster-snapshot-ravendb", steps["publish-k8s"]);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static RavenDBClusterDeployment Deployment(RavenDBIngressController ingress = RavenDBIngressController.Traefik)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var options = new RavenDBClusterOptions { Domain = "ravendb.example.test", Nodes = 3, IngressController = ingress };
        options.WithCertificates("ravendb-server", "ravendb-admin");

        return new RavenDBClusterDeployment(builder.AddRavenDB("ravendb").Resource, options) { BootstrapJobName = Job };
    }

    private static KubectlResult Ok(string output = "") => new(0, output, string.Empty);

    private static KubectlResult Fail(string error) => new(1, string.Empty, error);

    private static KubectlResult Unknown(string type) => Fail($"error: the server doesn't have a resource type \"{type}\"");

    /// <summary>Answers kubectl calls by the first rule whose text occurs in the call, in turn; the last answer repeats.</summary>
    private sealed class FakeKubectl : IKubectl
    {
        private readonly List<(string Match, Queue<KubectlResult> Answers)> _rules = [];
        private Exception? _failure;

        public List<string> Calls { get; } = [];

        public FakeKubectl On(string match, params KubectlResult[] answers)
        {
            _rules.Add((match, new Queue<KubectlResult>(answers)));
            return this;
        }

        public FakeKubectl Throw(Exception failure)
        {
            _failure = failure;
            return this;
        }

        public Task<KubectlResult> TryRunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            var call = string.Join(' ', arguments);
            Calls.Add(call);

            if (_failure is not null)
            {
                throw _failure;
            }

            foreach (var (match, answers) in _rules)
            {
                if (call.Contains(match, StringComparison.Ordinal))
                {
                    return Task.FromResult(answers.Count > 1 ? answers.Dequeue() : answers.Peek());
                }
            }

            return Task.FromResult(Fail($"unexpected call: {call}"));
        }
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
