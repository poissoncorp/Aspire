using Aspire.Hosting;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Cloud.Tests;

public class RavenDBCloudPublishTests
{
    [Fact]
    public async Task ComposePublishLeavesTheServerOutAndReferencesTheProductUrl()
    {
        var output = Directory.CreateTempSubdirectory(".ravendb-cloud-publish-test");

        try
        {
            using var builder = TestDistributedApplicationBuilder.Create(
                "AppHost:Operation=publish", $"Pipeline:OutputPath={output.FullName}", "Pipeline:Step=publish");

            builder.Configuration["Parameters:ravendb-cloud-api-key"] = "test-api-key";
            builder.AddDockerComposeEnvironment("compose");

            var apiKey = builder.AddParameter("ravendb-cloud-api-key", secret: true);
            var orders = builder.AddRavenDB("ravendb")
                .PublishAsRavenDBCloud(apiKey, cloud => cloud.WithAllowedIps("203.0.113.0/24"))
                .AddDatabase("orders", ensureCreated: true);

            builder.AddContainer("consumer", "busybox").WithReference(orders).WaitFor(orders);

            using var app = builder.Build();
            await app.RunAsync(TestContext.Current.CancellationToken);

            var compose = File.ReadAllText(Path.Combine(output.FullName, "docker-compose.yaml"));

            Assert.DoesNotContain("ravendb/ravendb", compose);
            Assert.DoesNotContain("ravendb-bootstrap", compose);
            Assert.Contains("ConnectionStrings__orders: \"URL=${RAVENDB_URL};Database=orders\"", compose);
            Assert.DoesNotContain("depends_on", compose);
            Assert.Contains("RAVENDB_URL=", File.ReadAllText(Path.Combine(output.FullName, ".env")));
        }
        finally
        {
            output.Delete(recursive: true);
        }
    }

    [Fact]
    public void PublishModeAddsTheCloudStepsAndTakesTheServerOutOfTheManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var server = builder.AddRavenDB("ravendb")
            .PublishAsRavenDBCloud(builder.AddParameter("ravendb-cloud-api-key", secret: true));

        using var app = builder.Build();

        Assert.Contains(ManifestPublishingCallbackAnnotation.Ignore, server.Resource.Annotations);
        Assert.Single(server.Resource.Annotations.OfType<RavenDBCloudDeployment>());
        Assert.NotNull(server.Resource.ExternalUrl);
        Assert.NotNull(app.Services.GetService<IRavenDBCloudApiClientFactory>());
    }

    [Fact]
    public void RunModeKeepsTheLocalContainer()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var server = builder.AddRavenDB("ravendb")
            .PublishAsRavenDBCloud(builder.AddParameter("ravendb-cloud-api-key", secret: true));

        Assert.DoesNotContain(ManifestPublishingCallbackAnnotation.Ignore, server.Resource.Annotations);
        Assert.Empty(server.Resource.Annotations.OfType<RavenDBCloudDeployment>());
        Assert.Null(server.Resource.ExternalUrl);
    }

    [Fact]
    public void ProductNameDefaultsToResourceAndEnvironment()
    {
        var deployment = CreateDeployment("Staging");

        Assert.Equal("ravendb-staging", deployment.ProductName);
        Assert.Equal("RAVENDB_URL", RavenDBCloudPipelineSteps.ToEnvironmentVariableName(deployment.Endpoint.ValueExpression));
    }

    [Fact]
    public void StepsHaveStableNamesAndDependencies()
    {
        var deployment = CreateDeployment("Production");

        var steps = RavenDBCloudPipelineSteps.Create(deployment).ToDictionary(s => s.Name);

        var provision = steps["ravendb-cloud-provision-ravendb"];
        Assert.Contains(WellKnownPipelineSteps.DeployPrereq, provision.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Deploy, provision.RequiredBySteps);
        Assert.Contains(WellKnownPipelineTags.ProvisionInfrastructure, provision.Tags);

        Assert.Contains("ravendb-cloud-provision-ravendb", steps["ravendb-cloud-databases-ravendb"].DependsOnSteps);
        Assert.Contains("ravendb-cloud-provision-ravendb", steps["ravendb-cloud-configure-ravendb"].DependsOnSteps);

        var destroy = steps["ravendb-cloud-destroy-ravendb"];
        Assert.Contains(WellKnownPipelineSteps.DestroyPrereq, destroy.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Destroy, destroy.RequiredBySteps);
    }

    [Fact]
    public void StepsAreOrderedAgainstTheComputeEnvironmentSteps()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddDockerComposeEnvironment("compose");
        var deployment = CreateDeployment(builder, "Production");

        PipelineStep Placeholder(string name) => new() { Name = name, Action = _ => Task.CompletedTask };

        var steps = RavenDBCloudPipelineSteps.Create(deployment)
            .Concat([
                Placeholder(WellKnownPipelineSteps.ProcessParameters),
                Placeholder("prepare-compose"),
                Placeholder("docker-compose-up-compose"),
                Placeholder("destroy-compose-compose"),
                Placeholder("provision-api-containerapp"),
            ])
            .ToDictionary(s => s.Name);

        using var services = new ServiceCollection().BuildServiceProvider();

        RavenDBCloudPipelineSteps.Configure(deployment, new PipelineConfigurationContext
        {
            Services = services,
            Steps = [.. steps.Values],
            Model = new DistributedApplicationModel(builder.Resources),
        });

        Assert.Contains(WellKnownPipelineSteps.ProcessParameters, steps["ravendb-cloud-provision-ravendb"].DependsOnSteps);
        Assert.Contains("prepare-compose", steps["ravendb-cloud-configure-ravendb"].DependsOnSteps);
        Assert.Contains("ravendb-cloud-configure-ravendb", steps["docker-compose-up-compose"].DependsOnSteps);
        Assert.Contains("ravendb-cloud-provision-ravendb", steps["provision-api-containerapp"].DependsOnSteps);

        // The application stops before the product is terminated.
        Assert.Contains("destroy-compose-compose", steps["ravendb-cloud-destroy-ravendb"].DependsOnSteps);
    }

    private static RavenDBCloudDeployment CreateDeployment(string environmentName) =>
        CreateDeployment(DistributedApplication.CreateBuilder(), environmentName);

    private static RavenDBCloudDeployment CreateDeployment(IDistributedApplicationBuilder builder, string environmentName)
    {
        var server = builder.AddRavenDB("ravendb");

        return new RavenDBCloudDeployment(
            server.Resource,
            builder.AddParameter("ravendb-cloud-api-key", secret: true).Resource,
            new RavenDBCloudOptions(),
            environmentName);
    }
}
