#pragma warning disable ASPIREPIPELINES004 // IPipelineOutputService is experimental

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

            // The client certificate is a file the deploy step writes next to the compose file.
            Assert.Contains("Aspire__RavenDB__Client__orders__CertificatePath: \"/run/secrets/ravendb-ravendb.pfx\"", compose);
            Assert.Contains("""
                    secrets:
                      - source: "ravendb-ravendb--consumer-certificate"
                        target: "ravendb-ravendb.pfx"
                """.ReplaceLineEndings("\n"), compose.ReplaceLineEndings("\n"));
            Assert.Contains("""
                secrets:
                  ravendb-ravendb--consumer-certificate:
                    file: "./ravendb-certs/ravendb--consumer.pfx"
                """.ReplaceLineEndings("\n"), compose.ReplaceLineEndings("\n"));
        }
        finally
        {
            output.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ApplicationThatBringsItsCertificateGetsNoneIssued()
    {
        var output = Directory.CreateTempSubdirectory(".ravendb-cloud-publish-test");

        try
        {
            using var builder = TestDistributedApplicationBuilder.Create(
                "AppHost:Operation=publish", $"Pipeline:OutputPath={output.FullName}", "Pipeline:Step=publish");

            builder.Configuration["Parameters:ravendb-cloud-api-key"] = "test-api-key";
            builder.AddDockerComposeEnvironment("compose");

            var orders = builder.AddRavenDB("ravendb")
                .PublishAsRavenDBCloud(builder.AddParameter("ravendb-cloud-api-key", secret: true), cloud => cloud.WithAllowedIps("203.0.113.0/24"))
                .AddDatabase("orders");

            var owned = Path.Combine(output.FullName, "api.pfx");
            builder.AddContainer("api", "busybox").WithReference(orders).WithRavenDBClientCertificateFile(orders, owned);
            builder.AddContainer("worker", "busybox").WithReference(orders);

            using var app = builder.Build();
            await app.RunAsync(TestContext.Current.CancellationToken);

            var compose = File.ReadAllText(Path.Combine(output.FullName, "docker-compose.yaml")).ReplaceLineEndings("\n");

            // Relative to the compose file, next to which it lies.
            Assert.Contains("""
                  ravendb-ravendb--api-certificate:
                    file: "api.pfx"
                """.ReplaceLineEndings("\n"), compose);
            Assert.Contains("""
                  ravendb-ravendb--worker-certificate:
                    file: "./ravendb-certs/ravendb--worker.pfx"
                """.ReplaceLineEndings("\n"), compose);
            Assert.DoesNotContain("ravendb-certs/ravendb--api.pfx", compose);
        }
        finally
        {
            output.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CertificateBroughtAsAKubernetesSecretIsRejected()
    {
        var output = Directory.CreateTempSubdirectory(".ravendb-cloud-publish-test");

        try
        {
            using var builder = TestDistributedApplicationBuilder.Create(
                "AppHost:Operation=publish", $"Pipeline:OutputPath={output.FullName}", "Pipeline:Step=publish");

            builder.Configuration["Parameters:ravendb-cloud-api-key"] = "test-api-key";
            builder.AddDockerComposeEnvironment("compose");

            var server = builder.AddRavenDB("ravendb").PublishAsRavenDBCloud(builder.AddParameter("ravendb-cloud-api-key", secret: true));
            var orders = server.AddDatabase("orders");

            builder.AddContainer("api", "busybox")
                .WithReference(orders)
                .WithAnnotation(new RavenDBClientCertificateAnnotation(server.Resource, RavenDBClientCertificateSource.KubernetesSecret, "api-cert"));

            using var app = builder.Build();
            var exception = await Assert.ThrowsAnyAsync<Exception>(() => app.RunAsync(TestContext.Current.CancellationToken));

            Assert.Contains("as a Kubernetes Secret, which only Kubernetes mounts", exception.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            output.Delete(recursive: true);
        }
    }

    [Fact]
    public void ConsumersAreTheResourcesThatReferenceTheServerOrItsDatabases()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var server = builder.AddRavenDB("ravendb");
        var orders = server.AddDatabase("orders");
        server.AddDatabase("reports", "reports-db");
        var other = builder.AddRavenDB("other").AddDatabase("audit");

        builder.AddContainer("api", "busybox").WithReference(orders).WithReference(other);
        builder.AddContainer("worker", "busybox").WithReference(server);
        builder.AddContainer("unrelated", "busybox").WithReference(other);

        var consumers = RavenDBConsumers
            .Find(new DistributedApplicationModel(builder.Resources), server.Resource)
            .ToDictionary(c => c.Resource.Name);

        Assert.Equal(["api", "worker"], consumers.Keys.Order());
        Assert.Equal(["orders"], consumers["api"].ConnectionNames);
        Assert.Equal(["orders"], consumers["api"].Databases);

        // A reference to the server reaches every database declared on it.
        Assert.Equal(["ravendb"], consumers["worker"].ConnectionNames);
        Assert.Equal(["orders", "reports-db"], consumers["worker"].Databases);
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
    public void ProductNameDefaultsToAppHostResourceAndEnvironment()
    {
        var deployment = CreateDeployment("Staging");

        // Two AppHosts in one account, both with AddRavenDB("ravendb"), get products of their own.
        Assert.Equal("contoso-apphost-ravendb-staging", deployment.ProductName);
        Assert.Equal("RAVENDB_URL", CommunityToolkit.Aspire.Utils.ComposeEnvironmentVariables.NameOf(deployment.Endpoint.ValueExpression));
    }

    [Theory]
    [InlineData("orders", "orders")]
    [InlineData("My_Orders.Prod", "myordersprod")]
    public void SubdomainIsTheProductNameWhenItFits(string productName, string expected) =>
        Assert.Equal(expected, RavenDBCloudDeployment.DeriveSubdomain(productName));

    [Fact]
    public void SubdomainOfALongProductNameIsShortenedWithAHash()
    {
        var staging = CreateDeployment("Staging").Subdomain;
        var production = CreateDeployment("Production").Subdomain;

        Assert.StartsWith("contoso-", production);
        Assert.Equal(RavenDBCloudDeployment.MaxSubdomainLength, production.Length);
        Assert.True(RavenDBCloudDeployment.IsValidSubdomain(production));
        Assert.NotEqual(staging, production);
    }

    [Theory]
    [InlineData("-shop")]
    [InlineData("shop-")]
    [InlineData("my_shop")]
    [InlineData("fourteen-chars")]
    public void InvalidSubdomainIsRejectedRightAway(string subdomain)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var exception = Assert.Throws<ArgumentException>(() => builder.AddRavenDB("ravendb")
            .PublishAsRavenDBCloud(builder.AddParameter("ravendb-cloud-api-key", secret: true), cloud => cloud.Subdomain = subdomain));

        Assert.Equal("configure", exception.ParamName);
    }

    [Theory]
    [InlineData("http://api.cloud.example.com")]
    [InlineData("not a url")]
    public void ApiKeyOnlyGoesToAnHttpsEndpoint(string endpoint)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var exception = Assert.Throws<ArgumentException>(() => builder.AddRavenDB("ravendb")
            .PublishAsRavenDBCloud(builder.AddParameter("ravendb-cloud-api-key", secret: true), cloud => cloud.ApiEndpoint = endpoint));

        Assert.Contains("must be an https URL", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DottedAppHostAndEnvironmentNamesDoNotAddPartsToTheCertificateNames() =>
        Assert.Equal("aspire.contoso-apphost.prod-eu.", RavenDBCloudClientCertificates.NamePrefix("Contoso.AppHost", "prod.eu"));

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
        Assert.Contains("ravendb-cloud-databases-ravendb", steps["ravendb-cloud-certificates-ravendb"].DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Deploy, steps["ravendb-cloud-certificates-ravendb"].RequiredBySteps);

        var destroy = steps["ravendb-cloud-destroy-ravendb"];
        Assert.Contains(WellKnownPipelineSteps.DestroyPrereq, destroy.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Destroy, destroy.RequiredBySteps);
    }

    [Fact]
    public async Task StepsAreOrderedAgainstAspiresOwnSteps()
    {
        var output = Directory.CreateTempSubdirectory(".ravendb-cloud-steps-test");

        try
        {
            using var builder = TestDistributedApplicationBuilder.Create(
                "AppHost:Operation=publish", $"Pipeline:OutputPath={output.FullName}", "Pipeline:Step=publish");
            builder.Configuration["Parameters:ravendb-cloud-api-key"] = "test-api-key";
            builder.AddDockerComposeEnvironment("compose");
            builder.AddRavenDB("ravendb").PublishAsRavenDBCloud(builder.AddParameter("ravendb-cloud-api-key", secret: true));

            // Registered after the server's: sees the steps as the server left them.
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
            Assert.Contains(WellKnownPipelineSteps.ProcessParameters, steps["ravendb-cloud-provision-ravendb"]);
            Assert.Contains("prepare-compose", steps["ravendb-cloud-configure-ravendb"]);
            Assert.Contains("prepare-compose", steps["ravendb-cloud-certificates-ravendb"]);
            Assert.Contains("ravendb-cloud-configure-ravendb", steps["docker-compose-up-compose"]);
            Assert.Contains("ravendb-cloud-certificates-ravendb", steps["docker-compose-up-compose"]);

            // The application stops before the product is terminated.
            Assert.Contains("destroy-compose-compose", steps["ravendb-cloud-destroy-ravendb"]);
        }
        finally
        {
            output.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("kubernetes")]
    [InlineData("container-apps")]
    public void ApplicationOutsideDockerComposeIsRejectedBeforeAnythingIsProvisioned(string target)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        IComputeEnvironmentResource environment = target == "kubernetes"
            ? builder.AddKubernetesEnvironment("elsewhere").Resource
            : builder.AddAzureContainerAppEnvironment("elsewhere").Resource;
        var deployment = CreateDeployment(builder, "Production");

        builder.AddContainer("api", "busybox")
            .WithReference(builder.CreateResourceBuilder(deployment.Server))
            .WithAnnotation(new DeploymentTargetAnnotation(environment) { ComputeEnvironment = environment });

        using var services = new ServiceCollection().BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(() => RavenDBCloudPipelineSteps.Configure(deployment, new PipelineConfigurationContext
        {
            Services = services,
            Steps = [.. RavenDBCloudPipelineSteps.Create(deployment)],
            Model = new DistributedApplicationModel(builder.Resources),
        }));

        Assert.StartsWith("'api' is deployed to 'elsewhere', where this integration does not deliver", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UrlThatAspireWroteNoVariableForStopsTheDeployment()
    {
        var output = Directory.CreateTempSubdirectory(".ravendb-cloud-env-test");

        try
        {
            using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
            builder.AddDockerComposeEnvironment("compose");
            var deployment = CreateDeployment(builder, "Production");
            deployment.Endpoint.Url = "https://a.contoso.ravendb.cloud";
            builder.AddContainer("api", "busybox").WithReference(builder.CreateResourceBuilder(deployment.Server));

            // As if Aspire had renamed the variable.
            File.WriteAllText(Path.Combine(output.FullName, ".env"), "RAVENDB_ENDPOINT=\n");

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => RavenDBCloudPipelineSteps.PatchEnvironmentFilesAsync(
                deployment,
                new DistributedApplicationModel(builder.Resources),
                new FixedOutputService(output.FullName),
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                TestContext.Current.CancellationToken));

            Assert.Contains("Aspire wrote no RAVENDB_URL", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            output.Delete(recursive: true);
        }
    }

    [Fact]
    public void RegionHasADefaultOnAwsOnly()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apiKey = builder.AddParameter("cloud-key", secret: true);

        Assert.Equal("us-east-1", CreateDeployment(builder, "Production").Region);

        var exception = Assert.Throws<ArgumentException>(() => builder.AddRavenDB("azure").PublishAsRavenDBCloud(apiKey, cloud => cloud.Provider = RavenDBCloudProvider.Azure));
        Assert.Contains("Set Region for RavenDB Cloud server 'azure': there is no default region on Azure.", exception.Message, StringComparison.Ordinal);

        builder.AddRavenDB("westeurope").PublishAsRavenDBCloud(apiKey, cloud =>
        {
            cloud.Provider = RavenDBCloudProvider.Azure;
            cloud.Region = "westeurope";
        });
    }

    [Fact]
    public void AppHostIsNamedAfterItsPackageOrItsProject()
    {
        var directory = Directory.CreateTempSubdirectory(".ravendb-cloud-apphost-test");

        try
        {
            // A C# AppHost: its project's name, whatever folder the repository is checked out to.
            Assert.Equal("Contoso.AppHost", RavenDBCloudBuilderExtensions.AppHostName(directory.FullName, "Contoso.AppHost"));

            // A TypeScript AppHost, whose process is always aspire-managed: its package.
            File.WriteAllText(Path.Combine(directory.FullName, "package.json"), """{"name":"@contoso/shop-apphost","private":true}""");
            Assert.Equal("contoso-shop-apphost", RavenDBCloudBuilderExtensions.AppHostName(directory.FullName, "aspire-managed"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void PackageOfAnotherVersionIsRejectedBeforeItsInternalsAreReached()
    {
        // Built together, these two match.
        CommunityToolkit.Aspire.Utils.MatchingPackageVersion.Ensure(typeof(RavenDBServerResource), typeof(RavenDBCloudOptions));

        var exception = Assert.Throws<InvalidOperationException>(() => CommunityToolkit.Aspire.Utils.MatchingPackageVersion.Ensure(typeof(string), typeof(RavenDBCloudOptions)));
        Assert.Contains("Install the same version of both packages", exception.Message, StringComparison.Ordinal);
    }

    private sealed class FixedOutputService(string directory) : IPipelineOutputService
    {
        public string GetOutputDirectory() => directory;

        public string GetOutputDirectory(IResource resource) => Path.Combine(directory, resource.Name);

        public string GetTempDirectory() => Path.GetTempPath();

        public string GetTempDirectory(IResource resource) => Path.Combine(Path.GetTempPath(), resource.Name);
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
            "Contoso.AppHost",
            environmentName);
    }
}
