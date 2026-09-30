#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental

using Aspire.Hosting;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging;
using YamlDotNet.RepresentationModel;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Tests;

public class RavenDBPublishTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ComposePublishAddsHealthCheckAndHealthyDependency()
    {
        using var tempDir = new TempDirectory();
        using var builder = CreateForPublish(tempDir.Path);

        builder.AddDockerComposeEnvironment("compose");
        var ravendb = builder.AddRavenDB("ravendb").WithDataVolume();
        var orders = ravendb.AddDatabase("orders", ensureCreated: true);

        builder.AddContainer("consumer", "busybox")
            .WithReference(orders)
            .WaitFor(ravendb);

        using var app = builder.Build();
        await app.RunAsync(TestContext.Current.CancellationToken);

        var services = ReadComposeServices(tempDir.Path);

        var healthCheck = (YamlSequenceNode)((YamlMappingNode)services["ravendb"])["healthcheck"]["test"];
        Assert.Equal("CMD-SHELL", healthCheck.Children[0].ToString());
        Assert.Equal("curl -sf http://127.0.0.1:8080/build/version > /dev/null || exit 1", healthCheck.Children[1].ToString());

        Assert.Equal("service_healthy", services["consumer"]["depends_on"]["ravendb"]["condition"].ToString());

        // As in run mode, the application starts once its databases exist.
        Assert.Equal("service_completed_successfully", services["consumer"]["depends_on"]["ravendb-bootstrap"]["condition"].ToString());
    }

    [Fact]
    public async Task TwoServersPublishTogether()
    {
        using var tempDir = new TempDirectory();
        using var builder = CreateForPublish(tempDir.Path);
        var logs = CaptureLogs(builder);

        builder.Configuration["Parameters:reports-url"] = "https://a.reports.example.com";
        builder.AddDockerComposeEnvironment("compose");
        var orders = builder.AddRavenDB("orders").AddDatabase("orders-db", ensureCreated: true);
        var reports = builder.AddRavenDB("reports").PublishAsExisting(builder.AddParameter("reports-url")).AddDatabase("reports-db");

        builder.AddContainer("consumer", "busybox").WithReference(orders).WithReference(reports);

        using var app = builder.Build();
        await app.RunAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error);
        Assert.True(ReadComposeServices(tempDir.Path).Children.ContainsKey(new YamlScalarNode("orders")));
    }

    [Fact]
    public async Task ComposePublishCreatesOnlyEnsureCreatedDatabases()
    {
        using var tempDir = new TempDirectory();
        using var builder = CreateForPublish(tempDir.Path);

        builder.AddDockerComposeEnvironment("compose");
        var ravendb = builder.AddRavenDB("ravendb");
        ravendb.AddDatabase("orders", ensureCreated: true);
        ravendb.AddDatabase("catalog", "catalog-db", ensureCreated: true);
        ravendb.AddDatabase("reports");

        using var app = builder.Build();
        await app.RunAsync(TestContext.Current.CancellationToken);

        var services = ReadComposeServices(tempDir.Path);
        var bootstrap = (YamlMappingNode)services["ravendb-bootstrap"];

        Assert.Equal("docker.io/ravendb/ravendb:6.2-latest", bootstrap["image"].ToString());
        Assert.Equal("/bin/sh", ((YamlSequenceNode)bootstrap["entrypoint"]).Children[0].ToString());
        Assert.Equal("service_healthy", bootstrap["depends_on"]["ravendb"]["condition"].ToString());

        var script = ((YamlSequenceNode)bootstrap["command"]).Children[1].ToString();
        Assert.Contains("http://ravendb:8080/admin/databases?name=orders", script);
        Assert.Contains("http://ravendb:8080/admin/databases?name=catalog-db", script);
        Assert.DoesNotContain("reports", script);

        // A database that could not be created fails the service.
        Assert.Contains("creating database orders failed' >&2; exit 1", script);
    }

    [Fact]
    public async Task ComposePublishWithoutEnsureCreatedHasNoBootstrap()
    {
        using var tempDir = new TempDirectory();
        using var builder = CreateForPublish(tempDir.Path);

        builder.AddDockerComposeEnvironment("compose");
        builder.AddRavenDB("ravendb").AddDatabase("orders");

        using var app = builder.Build();
        await app.RunAsync(TestContext.Current.CancellationToken);

        var services = ReadComposeServices(tempDir.Path);

        Assert.False(services.Children.ContainsKey(new YamlScalarNode("ravendb-bootstrap")));
    }

    [Fact]
    public async Task ComposePublishKeepsLicenseParameterOutOfTheComposeFile()
    {
        using var tempDir = new TempDirectory();
        using var builder = CreateForPublish(tempDir.Path);

        builder.Configuration["Parameters:ravendb-license"] = "{\"Id\":\"test\"}";
        builder.AddDockerComposeEnvironment("compose");
        var license = builder.AddParameter("ravendb-license", secret: true);
        builder.AddRavenDB("ravendb").WithLicense(license);

        using var app = builder.Build();
        await app.RunAsync(TestContext.Current.CancellationToken);

        var services = ReadComposeServices(tempDir.Path);
        var environment = services["ravendb"]["environment"];

        Assert.Equal("${RAVENDB_LICENSE}", environment["RAVEN_License"].ToString());
        Assert.Equal("true", environment["RAVEN_License_Eula_Accepted"].ToString());
        Assert.Contains("RAVENDB_LICENSE=", File.ReadAllText(Path.Combine(tempDir.Path, ".env")));
    }

    [Fact]
    public async Task KubernetesPublishDoesNotAddComposeBehaviour()
    {
        using var tempDir = new TempDirectory();
        using var builder = CreateForPublish(tempDir.Path);

        builder.AddKubernetesEnvironment("k8s");
        builder.AddRavenDB("ravendb").AddDatabase("orders", ensureCreated: true);

        using var app = builder.Build();
        await app.RunAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(tempDir.Path, "templates", "ravendb", "statefulset.yaml")) ||
                    File.Exists(Path.Combine(tempDir.Path, "templates", "ravendb", "deployment.yaml")));
        Assert.False(Directory.Exists(Path.Combine(tempDir.Path, "templates", "ravendb-bootstrap")));
    }

    [Fact]
    public async Task PublishFailsForUnsecuredServerWithExternalEndpoint()
    {
        using var tempDir = new TempDirectory();
        using var builder = CreateForPublish(tempDir.Path);

        var logs = CaptureLogs(builder);

        builder.AddDockerComposeEnvironment("compose");
        builder.AddRavenDB("ravendb").WithExternalHttpEndpoints();

        using var app = builder.Build();
        await app.RunAsync(TestContext.Current.CancellationToken);

        AssertPublishFailed(logs, "is unsecured but published with an external endpoint");
        Assert.False(File.Exists(Path.Combine(tempDir.Path, "docker-compose.yaml")));
    }

    [Fact]
    public async Task PublishFailsForAzureContainerAppsWithDataVolume()
    {
        using var tempDir = new TempDirectory();
        using var builder = CreateForPublish(tempDir.Path);

        var logs = CaptureLogs(builder);

        builder.AddAzureContainerAppEnvironment("aca");
        builder.AddRavenDB("ravendb").WithDataVolume();

        using var app = builder.Build();
        await app.RunAsync(TestContext.Current.CancellationToken);

        AssertPublishFailed(logs, "whose only persistent storage is Azure Files");
    }

    [Fact]
    public async Task PublishFailsForInvalidDatabaseName()
    {
        using var tempDir = new TempDirectory();
        using var builder = CreateForPublish(tempDir.Path);

        var logs = CaptureLogs(builder);

        builder.AddDockerComposeEnvironment("compose");
        builder.AddRavenDB("ravendb").AddDatabase("orders", "orders'; rm -rf /", ensureCreated: true);

        using var app = builder.Build();
        await app.RunAsync(TestContext.Current.CancellationToken);

        AssertPublishFailed(logs, "is not a valid RavenDB database name");
    }

    [Fact]
    public async Task ComposePublishAsExistingDeploysNoServerAndPointsConsumersAtTheUrl()
    {
        using var tempDir = new TempDirectory();
        using var builder = CreateForPublish(tempDir.Path);
        var logs = CaptureLogs(builder);

        builder.Configuration["Parameters:ravendb-url"] = "https://a.ravendb.example.com";
        builder.AddDockerComposeEnvironment("compose");
        var url = builder.AddParameter("ravendb-url");
        var ravendb = builder.AddRavenDB("ravendb").WithDataVolume().PublishAsExisting(url);
        var orders = ravendb.AddDatabase("orders", ensureCreated: true);

        builder.AddContainer("consumer", "busybox")
            .WithReference(orders)
            .WaitFor(ravendb);

        using var app = builder.Build();
        await app.RunAsync(TestContext.Current.CancellationToken);

        var services = ReadComposeServices(tempDir.Path);

        Assert.False(services.Children.ContainsKey(new YamlScalarNode("ravendb")));
        Assert.False(services.Children.ContainsKey(new YamlScalarNode("ravendb-bootstrap")));

        var consumer = (YamlMappingNode)services["consumer"];
        Assert.Equal("URL=${RAVENDB_URL};Database=orders", consumer["environment"]["ConnectionStrings__orders"].ToString());
        Assert.Equal("${RAVENDB_URL}", consumer["environment"]["ORDERS_URI"].ToString());
        Assert.False(consumer.Children.ContainsKey(new YamlScalarNode("depends_on")));

        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Text.Contains("are not created by the deployment", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task ComposePublishMountsTheCertificateTheServerOwnerIssued()
    {
        using var tempDir = new TempDirectory();
        using var builder = CreateForPublish(tempDir.Path);
        var logs = CaptureLogs(builder);

        builder.Configuration["Parameters:ravendb-url"] = "https://a.ravendb.example.com";
        builder.AddDockerComposeEnvironment("compose");
        var ravendb = builder.AddRavenDB("ravendb").PublishAsExisting(builder.AddParameter("ravendb-url"));
        var orders = ravendb.AddDatabase("orders");

        builder.AddContainer("consumer", "busybox")
            .WithReference(orders)
            .WithRavenDBClientCertificateFile(orders, "certs/consumer.pfx");

        using var app = builder.Build();
        await app.RunAsync(TestContext.Current.CancellationToken);

        var yaml = new YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(Path.Combine(tempDir.Path, "docker-compose.yaml"))));
        var compose = (YamlMappingNode)yaml.Documents[0].RootNode;

        // Given relative to the AppHost; Docker Compose resolves it relative to the compose file.
        var file = compose["secrets"]["ravendb-ravendb--consumer-certificate"]["file"].ToString();
        var certificate = Path.GetFullPath("certs/consumer.pfx", builder.AppHostDirectory);
        Assert.Equal(Path.GetRelativePath(tempDir.Path, certificate).Replace('\\', '/'), file);

        var consumer = (YamlMappingNode)compose["services"]["consumer"];
        Assert.Equal("ravendb-ravendb--consumer-certificate", consumer["secrets"][0]["source"].ToString());
        Assert.Equal("/run/secrets/ravendb-ravendb.pfx", consumer["environment"]["Aspire__RavenDB__Client__orders__CertificatePath"].ToString());

        // The owner has not handed the file over yet: publishing works, starting would not.
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Text.Contains("does not exist yet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ComposePublishMountsThePrivateCertificateAuthorityOfTheServer()
    {
        using var tempDir = new TempDirectory();
        using var builder = CreateForPublish(tempDir.Path);

        builder.Configuration["Parameters:ravendb-url"] = "https://a.ravendb.example.com";
        builder.AddDockerComposeEnvironment("compose");
        var orders = builder.AddRavenDB("ravendb").PublishAsExisting(builder.AddParameter("ravendb-url")).AddDatabase("orders");

        builder.AddContainer("consumer", "busybox")
            .WithReference(orders)
            .WithRavenDBClientCertificateFile(orders, "certs/consumer.pfx", "certs/ca.crt");

        using var app = builder.Build();
        await app.RunAsync(TestContext.Current.CancellationToken);

        var yaml = new YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(Path.Combine(tempDir.Path, "docker-compose.yaml"))));
        var compose = (YamlMappingNode)yaml.Documents[0].RootNode;

        var authority = Path.GetFullPath("certs/ca.crt", builder.AppHostDirectory);
        Assert.Equal(
            Path.GetRelativePath(tempDir.Path, authority).Replace('\\', '/'),
            compose["secrets"]["ravendb-ravendb--consumer-certificate-authority"]["file"].ToString());

        // The application trusts the authority next to the image's own roots.
        var consumer = (YamlMappingNode)compose["services"]["consumer"];
        var targets = ((YamlSequenceNode)consumer["secrets"]).Select(s => s["target"].ToString()).ToList();
        Assert.Contains("/ravendb/ravendb-ca/ca.crt", targets);
        Assert.Equal("/etc/ssl/certs:/ravendb/ravendb-ca", consumer["environment"]["SSL_CERT_DIR"].ToString());
    }

    [Fact]
    public void ASecondCertificateForTheSameServerIsRejected()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var orders = builder.AddRavenDB("ravendb").AddDatabase("orders");
        var api = builder.AddContainer("api", "busybox").WithRavenDBClientCertificateFile(orders, "certs/api.pfx");

        var exception = Assert.Throws<InvalidOperationException>(() => api.WithRavenDBClientCertificateFile(orders, "certs/other.pfx"));

        Assert.Contains("'api' already has a client certificate for RavenDB server 'ravendb'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishFailsForACertificateOfAServerTheApplicationDoesNotReference()
    {
        using var tempDir = new TempDirectory();
        using var builder = CreateForPublish(tempDir.Path);

        builder.AddDockerComposeEnvironment("compose");
        var orders = builder.AddRavenDB("ravendb").AddDatabase("orders");
        builder.AddContainer("consumer", "busybox").WithRavenDBClientCertificateFile(orders, "consumer.pfx");

        using var app = builder.Build();
        await Assert.ThrowsAsync<DistributedApplicationException>(() => app.RunAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task KubernetesPublishAsExistingDeploysNoServer()
    {
        using var tempDir = new TempDirectory();
        using var builder = CreateForPublish(tempDir.Path);

        builder.Configuration["Parameters:ravendb-url"] = "https://a.ravendb.example.com";
        builder.AddKubernetesEnvironment("k8s");
        var ravendb = builder.AddRavenDB("ravendb").PublishAsExisting(builder.AddParameter("ravendb-url"));
        builder.AddContainer("consumer", "busybox").WithReference(ravendb.AddDatabase("orders"));

        using var app = builder.Build();
        await app.RunAsync(TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(Path.Combine(tempDir.Path, "templates", "ravendb")));
        Assert.Contains("ConnectionStrings__orders", File.ReadAllText(Path.Combine(tempDir.Path, "templates", "consumer", "config.yaml")));
    }

    [Fact]
    public async Task AzureContainerAppsPublishAsExistingSucceeds()
    {
        using var tempDir = new TempDirectory();
        using var builder = CreateForPublish(tempDir.Path);
        var logs = CaptureLogs(builder);

        builder.AddAzureContainerAppEnvironment("aca");
        var ravendb = builder.AddRavenDB("ravendb").WithDataVolume().PublishAsExisting(builder.AddParameter("ravendb-url"));
        builder.AddContainer("consumer", "busybox").WithReference(ravendb.AddDatabase("orders"));

        using var app = builder.Build();
        await app.RunAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error);
        Assert.False(File.Exists(Path.Combine(tempDir.Path, "ravendb", "ravendb.bicep")));
        Assert.Contains("ravendb_url", File.ReadAllText(Path.Combine(tempDir.Path, "consumer", "consumer.bicep")));
    }

    [Fact]
    public void RunModeIgnoresPublishAsExisting()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var ravendb = builder.AddRavenDB("ravendb").PublishAsExisting(builder.AddParameter("ravendb-url"));

        Assert.Null(ravendb.Resource.ExternalUrl);
        Assert.DoesNotContain(ManifestPublishingCallbackAnnotation.Ignore, ravendb.Resource.Annotations);
    }

    [Fact]
    public void RunModeRegistersNoPublishingBehaviour()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        builder.AddDockerComposeEnvironment("compose");
        builder.AddRavenDB("ravendb").AddDatabase("orders", ensureCreated: true);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var server = Assert.Single(model.Resources.OfType<RavenDBServerResource>());

        Assert.Empty(server.Annotations.OfType<PipelineStepAnnotation>());
        Assert.Empty(server.Annotations.OfType<DockerComposeServiceCustomizationAnnotation>());
    }

    [Fact]
    public void BootstrapScriptHasNoShellVariables()
    {
        var script = RavenDBPublishing.BuildBootstrapScript("http://ravendb:8080", ["orders", "orders", "catalog-db"]);

        // Docker Compose interpolates $VAR in the file, so the script must not rely on shell variables.
        Assert.DoesNotContain("$", script);
        Assert.Equal(1, CountOccurrences(script, "name=orders&replicationFactor=1"));
        Assert.Contains("if curl -sf 'http://ravendb:8080/databases?name=catalog-db'", script);
        Assert.EndsWith("echo 'ravendb-bootstrap: done'", script);
    }

    [Fact]
    public void WithLicenseParameterClearsLiteralLicenseWarning()
    {
        var builder = DistributedApplication.CreateBuilder();

        var settings = RavenDBServerSettings.Unsecured();
        settings.WithLicense("literal-license");

        var ravendb = builder.AddRavenDB("ravendb", settings);
        Assert.True(ravendb.Resource.HasLiteralLicense);

        ravendb.WithLicense(builder.AddParameter("ravendb-license", secret: true));
        Assert.False(ravendb.Resource.HasLiteralLicense);
    }

    /// <summary>
    /// A failed pipeline step does not surface from <c>RunAsync</c>: the host logs it and stops. The captured logs
    /// are the observable outcome.
    /// </summary>
    private CapturingLoggerProvider CaptureLogs(IDistributedApplicationTestingBuilder builder)
    {
        var provider = new CapturingLoggerProvider(output);
        builder.Services.AddLogging(logging => logging.AddProvider(provider));
        return provider;
    }

    private static void AssertPublishFailed(CapturingLoggerProvider logs, string expectedMessage) =>
        Assert.Contains(logs.Entries, e => e.Level >= LogLevel.Error && e.Text.Contains(expectedMessage, StringComparison.Ordinal));

    private static IDistributedApplicationTestingBuilder CreateForPublish(string outputPath) =>
        TestDistributedApplicationBuilder.Create(
            "AppHost:Operation=publish", $"Pipeline:OutputPath={outputPath}", "Pipeline:Step=publish");

    private static YamlMappingNode ReadComposeServices(string outputPath)
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(File.ReadAllText(Path.Combine(outputPath, "docker-compose.yaml")));
        yaml.Load(reader);

        return (YamlMappingNode)((YamlMappingNode)yaml.Documents[0].RootNode)["services"];
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;

        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private sealed class CapturingLoggerProvider(ITestOutputHelper output) : ILoggerProvider
    {
        private readonly List<(LogLevel Level, string Text)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Text)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private void Add(LogLevel level, string text)
        {
            lock (_entries)
            {
                _entries.Add((level, text));
            }

            if (level >= LogLevel.Error)
            {
                output.WriteLine(text);
            }
        }

        private sealed class CapturingLogger(CapturingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                provider.Add(logLevel, $"{formatter(state, exception)} {exception}");
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory(".ravendb-publish-test");

        public string Path => _directory.FullName;

        public void Dispose() => _directory.Delete(recursive: true);
    }
}
