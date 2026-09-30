using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YamlDotNet.RepresentationModel;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Kubernetes.Tests;

public class RavenDBClusterPublishTests(ITestOutputHelper output)
{
    private const string PinnedImage = "ravendb/ravendb:7.2.6-ubuntu.24.04-x64";

    [Fact]
    public async Task ChartRunsTheClusterThroughTheOperator()
    {
        using var chart = await Publish(builder => AddCluster(builder).AddDatabase("orders", ensureCreated: true));

        Assert.False(Directory.Exists(Path.Combine(chart.Path, "templates", "ravendb")));

        var cluster = chart.Single("RavenDBCluster");
        Assert.Equal("ravendb.ravendb.io/v1", cluster.Scalar("apiVersion"));
        Assert.Equal("ravendb", cluster.Scalar("metadata", "name"));
        Assert.Equal(PinnedImage, cluster.Scalar("spec", "image"));
        Assert.Equal("None", cluster.Scalar("spec", "mode"));
        Assert.Equal("ravendb.example.test", cluster.Scalar("spec", "domain"));
        Assert.Equal("ravendb-license", cluster.Scalar("spec", "licenseSecretRef"));
        Assert.Equal("ravendb-admin", cluster.Scalar("spec", "clientCertSecretRef"));
        Assert.Equal("ravendb-server", cluster.Scalar("spec", "clusterCertSecretRef"));
        Assert.Equal("ravendb-ca", cluster.Scalar("spec", "caCertSecretRef"));
        Assert.Equal("10Gi", cluster.Scalar("spec", "storage", "data", "size"));
        Assert.Equal("ingress-controller", cluster.Scalar("spec", "externalAccessConfiguration", "type"));
        Assert.Equal("nginx", cluster.Scalar("spec", "externalAccessConfiguration", "ingressControllerContext", "ingressClassName"));

        var nodes = cluster.Items("spec", "nodes");
        Assert.Equal(["a", "b", "c"], nodes.Select(n => n.Scalar("tag")));
        Assert.Equal("https://b.ravendb.example.test:443", nodes[1].Scalar("publicServerUrl"));
        Assert.Equal("tcp://b-tcp.ravendb.example.test:443", nodes[1].Scalar("publicServerUrlTcp"));

        // nginx passes TLS through for the operator's own Ingress.
        Assert.Empty(chart.All("IngressRouteTCP"));

        // A license is JSON: base64 keeps its quotes out of the quoted YAML string.
        var license = chart.Single("Secret", "ravendb-license");
        Assert.Equal("{{ .Values.secrets.ravendb_bootstrap.ravendb_license | b64enc }}", license.Scalar("data", "license.json"));
    }

    [Fact]
    public async Task BootstrapJobCreatesTheDatabasesAndTheApplicationCertificates()
    {
        using var chart = await Publish(builder =>
        {
            var server = AddCluster(builder);
            var orders = server.AddDatabase("orders", ensureCreated: true);
            server.AddDatabase("reports", ensureCreated: true);
            server.AddDatabase("archive");

            builder.AddContainer("api", "busybox").WithReference(orders);
            builder.AddContainer("worker", "busybox").WithReference(server);
        });

        var job = chart.Single("Job");
        Assert.StartsWith("ravendb-bootstrap-", job.Scalar("metadata", "name"));

        var pod = job.Get("spec", "template", "spec");
        Assert.Equal("ravendb-bootstrap", pod.Scalar("serviceAccountName"));
        Assert.Equal("OnFailure", pod.Scalar("restartPolicy"));

        var container = pod.Items("containers")[0];
        Assert.Equal(PinnedImage, container.Scalar("image"));
        Assert.Equal(["/bin/bash"], container.Items("command").Select(n => ((YamlScalarNode)n).Value));
        Assert.Equal(["/ravendb/bootstrap/bootstrap.sh"], container.Items("args").Select(n => ((YamlScalarNode)n).Value));

        var volumes = pod.Items("volumes").ToDictionary(v => v.Scalar("name")!);
        Assert.Equal("ravendb-bootstrap-script", volumes["bootstrap-script"].Scalar("configMap", "name"));
        Assert.Equal("ravendb-admin", volumes["admin-certificate"].Scalar("secret", "secretName"));
        Assert.Equal("ravendb-ca", volumes["certificate-authority"].Scalar("secret", "secretName"));

        // Only the keys the Job needs, and its scratch space (keys included) in memory.
        Assert.Equal("client.pfx", volumes["admin-certificate"].Items("secret", "items").Single().Scalar("key"));
        Assert.Equal("ca.crt", volumes["certificate-authority"].Items("secret", "items").Single().Scalar("key"));
        Assert.Equal("Memory", volumes["scratch"].Scalar("emptyDir", "medium"));
        Assert.Equal("/tmp", container.Items("volumeMounts").Single(m => m.Scalar("name") == "scratch").Scalar("mountPath"));

        // The image's own non-root user, and nothing more.
        Assert.Equal("true", pod.Scalar("securityContext", "runAsNonRoot"));
        Assert.Equal("999", pod.Scalar("securityContext", "runAsUser"));
        Assert.Equal("RuntimeDefault", pod.Scalar("securityContext", "seccompProfile", "type"));
        Assert.Equal("false", container.Scalar("securityContext", "allowPrivilegeEscalation"));
        Assert.Equal("true", container.Scalar("securityContext", "readOnlyRootFilesystem"));
        Assert.Equal(["ALL"], container.Items("securityContext", "capabilities", "drop").Select(n => ((YamlScalarNode)n).Value));

        Assert.Equal(RavenDBClusterPublishing.Script, chart.Single("ConfigMap", "ravendb-bootstrap-script").Scalar("data", "bootstrap.sh"));

        var settings = chart.Values("config", "ravendb_bootstrap");
        Assert.Equal("https://a.ravendb.example.test:443 https://b.ravendb.example.test:443 https://c.ravendb.example.test:443", settings.Scalar("RAVENDB_URLS"));
        Assert.Equal("orders reports", settings.Scalar("RAVENDB_DATABASES"));
        Assert.Equal("3", settings.Scalar("RAVENDB_REPLICATION_FACTOR"));

        // The worker references the server: it gets every declared database.
        Assert.Equal(
            "ravendb-api-client-certificate=orders ravendb-worker-client-certificate=archive,orders,reports",
            settings.Scalar("RAVENDB_APPLICATIONS"));
        Assert.Equal("ravendb-bootstrap", settings.Scalar("RAVENDB_SECRET_OWNER"));

        // The license is for the operator: the Job gets no secrets at all.
        Assert.DoesNotContain(chart.All("Secret"), s => s.Scalar("metadata", "name") == "ravendb-bootstrap-secrets");
    }

    [Fact]
    public async Task BootstrapMayOnlyTouchTheApplicationsSecrets()
    {
        using var chart = await Publish(builder =>
        {
            var orders = AddCluster(builder).AddDatabase("orders");
            builder.AddContainer("api", "busybox").WithReference(orders);
        });

        Assert.Equal("ravendb-bootstrap", chart.Single("ServiceAccount").Scalar("metadata", "name"));

        var rules = chart.Single("Role", "ravendb-bootstrap-role").Items("rules")
            .Select(r => (
                Resource: r.Items("resources").Single().ToString(),
                Verb: r.Items("verbs").Single().ToString(),
                Names: r.Find("resourceNames") is YamlSequenceNode names ? string.Join(",", names) : ""))
            .ToList();

        Assert.Equal(
            [("serviceaccounts", "get", "ravendb-bootstrap"), ("secrets", "get", "ravendb-api-client-certificate"), ("secrets", "create", "")],
            rules);

        var binding = chart.Single("RoleBinding");
        Assert.Equal("ravendb-bootstrap-role", binding.Scalar("roleRef", "name"));
        var subject = binding.Items("subjects")[0];
        Assert.Equal("ravendb-bootstrap", subject.Scalar("name"));
        Assert.Equal("{{ .Release.Namespace }}", subject.Scalar("namespace"));
    }

    [Fact]
    public async Task ApplicationsMountTheirCertificateAndTrustTheClusterAuthority()
    {
        using var chart = await Publish(builder =>
        {
            var orders = AddCluster(builder).AddDatabase("orders", ensureCreated: true);
            builder.AddContainer("api", "busybox").WithReference(orders).WaitFor(orders);
        });

        Assert.Equal("URL=https://a.ravendb.example.test:443;Database=orders", chart.Values("config", "api").Scalar("ConnectionStrings__orders"));

        var pod = chart.Single("Deployment", "api-deployment").Get("spec", "template", "spec");
        var volumes = pod.Items("volumes").ToDictionary(v => v.Scalar("name")!);
        Assert.Equal("ravendb-api-client-certificate", volumes["ravendb-client-certificate"].Scalar("secret", "secretName"));
        Assert.Equal("ravendb-ca", volumes["ravendb-certificate-authority"].Scalar("secret", "secretName"));

        // A certificate authority Secret from cert-manager also holds the authority's key: only ca.crt is mounted.
        Assert.Equal("client.pfx", volumes["ravendb-client-certificate"].Items("secret", "items").Single().Scalar("key"));
        Assert.Equal("ca.crt", volumes["ravendb-certificate-authority"].Items("secret", "items").Single().Scalar("key"));

        var container = pod.Items("containers")[0];
        var mounts = container.Items("volumeMounts").ToDictionary(m => m.Scalar("name")!, m => m.Scalar("mountPath"));
        Assert.Equal("/ravendb/ravendb", mounts["ravendb-client-certificate"]);
        Assert.Equal("/ravendb/ravendb-ca", mounts["ravendb-certificate-authority"]);

        var environment = container.Items("env").ToDictionary(e => e.Scalar("name")!, e => e.Scalar("value"));
        Assert.Equal("/ravendb/ravendb/client.pfx", environment["Aspire__RavenDB__Client__orders__CertificatePath"]);
        Assert.Equal("/etc/ssl/certs:/ravendb/ravendb-ca", environment["SSL_CERT_DIR"]);
    }

    [Fact]
    public async Task TraefikPassesTlsThroughToEachNode()
    {
        using var chart = await Publish(builder => AddCluster(builder, cluster =>
        {
            cluster.Nodes = 2;
            cluster.IngressClassName = "traefik";
        }));

        Assert.Equal("traefik", chart.Single("RavenDBCluster").Scalar("spec", "externalAccessConfiguration", "ingressControllerContext", "ingressClassName"));

        var routes = chart.Single("IngressRouteTCP", "ravendb-nodes");
        Assert.Equal("traefik.io/v1alpha1", routes.Scalar("apiVersion"));
        Assert.Equal(["websecure"], routes.Items("spec", "entryPoints").Select(n => ((YamlScalarNode)n).Value));
        Assert.Equal("true", routes.Scalar("spec", "tls", "passthrough"));

        Assert.Equal(
            [
                ("HostSNI(`a.ravendb.example.test`)", "ravendb-a", "443"),
                ("HostSNI(`a-tcp.ravendb.example.test`)", "ravendb-a", "38888"),
                ("HostSNI(`b.ravendb.example.test`)", "ravendb-b", "443"),
                ("HostSNI(`b-tcp.ravendb.example.test`)", "ravendb-b", "38888"),
            ],
            routes.Items("spec", "routes").Select(r =>
            {
                var service = r.Items("services").Single();
                return (r.Scalar("match"), service.Scalar("name"), service.Scalar("port"));
            }));
    }

    [Fact]
    public async Task ClusterWorksNextToAnotherComputeEnvironment()
    {
        using var chart = await Publish(builder =>
        {
            var k8s = builder.Resources.OfType<KubernetesEnvironmentResource>().Single();
            builder.AddDockerComposeEnvironment("compose");

            var orders = AddCluster(builder).AddDatabase("orders");
            builder.AddContainer("api", "busybox").WithReference(orders).WithComputeEnvironment(builder.CreateResourceBuilder(k8s));
        }, chartDirectory: "k8s");

        Assert.Empty(chart.Errors);
        Assert.Single(chart.All("RavenDBCluster"));
        Assert.Single(chart.All("Job"));
        Assert.False(Directory.Exists(Path.Combine(chart.Path, "templates", "ravendb")));
    }

    [Fact]
    public async Task LetsEncryptNeedsNoCertificateAuthority()
    {
        using var chart = await Publish(builder =>
        {
            var orders = AddCluster(builder, cluster => cluster.WithLetsEncrypt("ops@example.test", "ravendb-admin")).AddDatabase("orders");
            builder.AddContainer("api", "busybox").WithReference(orders);
        });

        var cluster = chart.Single("RavenDBCluster");
        Assert.Equal("LetsEncrypt", cluster.Scalar("spec", "mode"));
        Assert.Equal("ops@example.test", cluster.Scalar("spec", "email"));
        Assert.Null(cluster.Find("spec", "clusterCertSecretRef"));
        Assert.Null(cluster.Find("spec", "caCertSecretRef"));

        var pod = chart.Single("Deployment", "api-deployment").Get("spec", "template", "spec");
        Assert.Single(pod.Items("volumes"));
        Assert.DoesNotContain("SSL_CERT_DIR", pod.ToString());
    }

    [Fact]
    public async Task LicenseMayComeFromAnExistingSecret()
    {
        using var chart = await Publish(builder =>
            builder.AddRavenDB("ravendb").PublishAsRavenDBCluster(cluster =>
            {
                ConfigureCluster(cluster);
                cluster.LicenseSecretName = "company-license";
            }));

        Assert.Equal("company-license", chart.Single("RavenDBCluster").Scalar("spec", "licenseSecretRef"));
        Assert.DoesNotContain(chart.All("Secret"), s => s.Scalar("metadata", "name") is "ravendb-license" or "company-license");
    }

    [Fact]
    public async Task BootstrapJobGetsANewNameWhenItsConfigurationChanges()
    {
        // Published into the same directory, as aspire deploy does: Aspire keeps the files of earlier publishes, and
        // the chart must still hold a single Job.
        var directory = Directory.CreateTempSubdirectory(".ravendb-cluster-publish-test");

        async Task<string?> JobName(params string[] databases)
        {
            using var chart = await Publish(builder =>
            {
                var server = AddCluster(builder);
                foreach (var database in databases)
                {
                    server.AddDatabase(database, ensureCreated: true);
                }
            }, into: directory);

            Assert.Empty(chart.Errors);
            return chart.Single("Job").Scalar("metadata", "name");
        }

        try
        {
            var first = await JobName("orders");

            Assert.Equal(first, await JobName("orders"));
            Assert.NotEqual(first, await JobName("orders", "reports"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ApplicationOfAnExistingServerMountsTheCertificateItsOwnerIssued()
    {
        using var chart = await Publish(builder =>
        {
            builder.Configuration["Parameters:ravendb-url"] = "https://a.ravendb.example.test:443";
            var orders = builder.AddRavenDB("ravendb").PublishAsExisting(builder.AddParameter("ravendb-url")).AddDatabase("orders");
            builder.AddContainer("api", "busybox").WithReference(orders).WithRavenDBClientCertificateSecret(orders, "api-orders", "ravendb-ca");
        });

        // Nothing is issued: no admin, no Job, no access to Secrets.
        Assert.Empty(chart.All("RavenDBCluster"));
        Assert.Empty(chart.All("Job"));
        Assert.Empty(chart.All("Role"));

        var pod = chart.Single("Deployment", "api-deployment").Get("spec", "template", "spec");
        var volumes = pod.Items("volumes").ToDictionary(v => v.Scalar("name")!);
        Assert.Equal("api-orders", volumes["ravendb-client-certificate"].Scalar("secret", "secretName"));
        Assert.Equal("ravendb-ca", volumes["ravendb-certificate-authority"].Scalar("secret", "secretName"));

        var environment = pod.Items("containers")[0].Items("env").ToDictionary(e => e.Scalar("name")!, e => e.Scalar("value"));
        Assert.Equal("/ravendb/ravendb/client.pfx", environment["Aspire__RavenDB__Client__orders__CertificatePath"]);
        Assert.Equal("/etc/ssl/certs:/ravendb/ravendb-ca", environment["SSL_CERT_DIR"]);
    }

    [Fact]
    public async Task BootstrapIssuesNoCertificateToAnApplicationThatBringsOne()
    {
        using var chart = await Publish(builder =>
        {
            var orders = AddCluster(builder).AddDatabase("orders");
            builder.AddContainer("api", "busybox").WithReference(orders).WithRavenDBClientCertificateSecret(orders, "api-orders");
            builder.AddContainer("worker", "busybox").WithReference(orders);
        });

        Assert.Equal("ravendb-worker-client-certificate=orders", chart.Values("config", "ravendb_bootstrap").Scalar("RAVENDB_APPLICATIONS"));

        var api = chart.Single("Deployment", "api-deployment").Get("spec", "template", "spec");
        Assert.Equal("api-orders", Assert.Single(api.Items("volumes"), v => v.Scalar("name") == "ravendb-client-certificate").Scalar("secret", "secretName"));
    }

    [Fact]
    public async Task CertificateForAServerTheApplicationDoesNotReferenceIsRejected()
    {
        using var chart = await Publish(builder =>
        {
            var orders = AddCluster(builder).AddDatabase("orders");
            builder.AddContainer("api", "busybox").WithRavenDBClientCertificateSecret(orders, "api-orders");
        });

        Assert.Contains(chart.Errors, e => e.Contains("'api' has a client certificate for RavenDB server 'ravendb' but does not reference it", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("no-environment", "the application has no Kubernetes environment")]
    [InlineData("floating-tag", "only runs pinned images")]
    [InlineData("no-license", "reads the license of 'ravendb' from a Secret")]
    [InlineData("two-clusters", "runs one cluster per namespace")]
    [InlineData("database-name", "'orders; rm' is not a valid RavenDB database name")]
    [InlineData("admin-secret", "'api' would get Secret 'ravendb-api-client-certificate'")]
    [InlineData("certificate-file", "brings its certificate for RavenDB server 'ravendb' as a file, which only Docker Compose mounts")]
    public async Task PublishRejectsWhatTheOperatorCannotRun(string scenario, string expectedError)
    {
        using var chart = await Publish(builder =>
        {
            switch (scenario)
            {
                case "no-environment":
                    AddCluster(builder);
                    break;
                case "floating-tag":
                    AddCluster(builder, cluster => cluster.Image = "ravendb/ravendb:7.2-latest");
                    break;
                case "no-license":
                    builder.AddRavenDB("ravendb").PublishAsRavenDBCluster(ConfigureCluster);
                    break;
                case "database-name":
                    AddCluster(builder).AddDatabase("orders", "orders; rm");
                    break;
                case "certificate-file":
                    var orders = AddCluster(builder).AddDatabase("orders");
                    builder.AddContainer("api", "busybox").WithReference(orders).WithRavenDBClientCertificateFile(orders, "api.pfx");
                    break;
                case "admin-secret":
                    var server = AddCluster(builder, cluster => cluster.WithCertificates("ravendb-server", "ravendb-api-client-certificate"));
                    builder.AddContainer("api", "busybox").WithReference(server);
                    break;
                case "two-clusters":
                    AddCluster(builder);
                    builder.Configuration["Parameters:reports-license"] = "{}";
                    builder.AddRavenDB("reports").WithLicense(builder.AddParameter("reports-license", secret: true)).PublishAsRavenDBCluster(ConfigureCluster);
                    break;
            }
        }, addEnvironment: scenario != "no-environment");

        Assert.Contains(chart.Errors, e => e.Contains(expectedError, StringComparison.Ordinal));
        Assert.Empty(chart.All("RavenDBCluster"));
    }

    [Theory]
    [InlineData("domain")]
    [InlineData("nodes")]
    [InlineData("certificates")]
    public void IncompleteOptionsAreRejectedRightAway(string missing)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var exception = Assert.Throws<ArgumentException>(() => builder.AddRavenDB("ravendb").PublishAsRavenDBCluster(cluster =>
        {
            cluster.Domain = missing == "domain" ? null : "ravendb.example.test";
            cluster.Nodes = missing == "nodes" ? 0 : 1;

            if (missing != "certificates")
            {
                cluster.WithCertificates("ravendb-server", "ravendb-admin");
            }
        }));

        Assert.Equal("configure", exception.ParamName);
    }

    [Theory]
    [InlineData("Ravendb.Example.com")]
    [InlineData("ravendb.example.com`) || HostSNI(`*")]
    [InlineData("-ravendb.example.com")]
    public void DomainThatIsNotADnsNameIsRejectedRightAway(string domain)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var exception = Assert.Throws<ArgumentException>(() => builder.AddRavenDB("ravendb").PublishAsRavenDBCluster(cluster =>
        {
            ConfigureCluster(cluster);
            cluster.Domain = domain;
        }));

        Assert.Contains("is not a domain the nodes can be published under", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("istio")]
    [InlineData("Traefik")]
    public void IngressControllerTheOperatorDoesNotSupportIsRejectedRightAway(string ingressClassName)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var exception = Assert.Throws<ArgumentException>(() => builder.AddRavenDB("ravendb").PublishAsRavenDBCluster(cluster =>
        {
            ConfigureCluster(cluster);
            cluster.IngressClassName = ingressClassName;
        }));

        Assert.Contains($"nginx, traefik or haproxy, not '{ingressClassName}'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RunModeKeepsTheLocalContainer()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var server = builder.AddRavenDB("ravendb").PublishAsRavenDBCluster(ConfigureCluster);

        Assert.Null(server.Resource.ExternalUrl);
        Assert.Empty(server.Resource.Annotations.OfType<RavenDBClusterDeployment>());
        Assert.DoesNotContain(builder.Resources, r => r.Name == "ravendb-bootstrap");
    }

    [Fact]
    public void StepsRunAfterTheChartIsWrittenAndAfterHelm()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddKubernetesEnvironment("k8s");
        var server = builder.AddRavenDB("ravendb");
        var deployment = new RavenDBClusterDeployment(server.Resource, Options());

        var steps = RavenDBClusterPipelineSteps.Create(deployment).ToList();
        var publish = Assert.Single(steps, s => s.Name == "ravendb-cluster-publish-ravendb");
        var watch = Assert.Single(steps, s => s.Name == "ravendb-cluster-watch-ravendb");
        var wait = Assert.Single(steps, s => s.Name == "ravendb-cluster-wait-ravendb");
        var reset = Assert.Single(steps, s => s.Name == "ravendb-cluster-reset-ravendb");
        var writeChart = new PipelineStep { Name = "publish-k8s", Action = _ => Task.CompletedTask };
        var prepareHelm = new PipelineStep { Name = "prepare-k8s", Action = _ => Task.CompletedTask };
        var helm = new PipelineStep { Name = "helm-deploy-k8s", Action = _ => Task.CompletedTask };

        using var services = new ServiceCollection().BuildServiceProvider();
        RavenDBClusterPipelineSteps.Configure(deployment, new PipelineConfigurationContext
        {
            Services = services,
            Steps = [.. steps, writeChart, prepareHelm, helm],
            Model = new DistributedApplicationModel(builder.Resources),
        });

        Assert.Contains("publish-k8s", publish.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Publish, publish.RequiredBySteps);

        // Next to Helm, so a cluster that does not get ready is reported before Helm times out.
        Assert.Contains("prepare-k8s", watch.DependsOnSteps);
        Assert.DoesNotContain("helm-deploy-k8s", watch.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Deploy, watch.RequiredBySteps);

        Assert.Contains("helm-deploy-k8s", wait.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Deploy, wait.RequiredBySteps);

        // A bootstrap Job that failed is deleted before Helm, which then creates it again.
        Assert.Contains("ravendb-cluster-reset-ravendb", helm.DependsOnSteps);
        Assert.Contains("prepare-k8s", reset.DependsOnSteps);
    }

    [Fact]
    public void ScriptSurvivesHelmAndLinux()
    {
        // Helm renders the chart as a template, and the script runs under bash in a Linux container.
        Assert.DoesNotContain("{{", RavenDBClusterPublishing.Script);
        Assert.DoesNotContain("\r", RavenDBClusterPublishing.Script);
        Assert.StartsWith("#!/bin/bash\n", RavenDBClusterPublishing.Script);
    }

    private static IResourceBuilder<RavenDBServerResource> AddCluster(
        IDistributedApplicationBuilder builder,
        Action<RavenDBClusterOptions>? configure = null) =>
        builder.AddRavenDB("ravendb")
            .WithLicense(builder.AddParameter("ravendb-license", secret: true))
            .PublishAsRavenDBCluster(cluster =>
            {
                ConfigureCluster(cluster);
                configure?.Invoke(cluster);
            });

    private static void ConfigureCluster(RavenDBClusterOptions cluster)
    {
        cluster.Domain = "ravendb.example.test";
        cluster.Nodes = 3;
        cluster.Image = PinnedImage;
        cluster.WithCertificates("ravendb-server", "ravendb-admin", "ravendb-ca");
    }

    private static RavenDBClusterOptions Options()
    {
        var options = new RavenDBClusterOptions();
        ConfigureCluster(options);
        return options;
    }

    private async Task<PublishedChart> Publish(
        Action<IDistributedApplicationTestingBuilder> configure,
        bool addEnvironment = true,
        DirectoryInfo? into = null,
        string? chartDirectory = null)
    {
        var directory = into ?? Directory.CreateTempSubdirectory(".ravendb-cluster-publish-test");
        var errors = new List<string>();

        using var builder = TestDistributedApplicationBuilder.Create(
            "AppHost:Operation=publish", $"Pipeline:OutputPath={directory.FullName}", "Pipeline:Step=publish");

        builder.Services.AddLogging(logging => logging.AddProvider(new ErrorCollector(errors, output)));
        builder.Configuration["Parameters:ravendb-license"] = "{\"Id\":\"x\"}";

        if (addEnvironment)
        {
            builder.AddKubernetesEnvironment("k8s");
        }

        configure(builder);

        try
        {
            using var app = builder.Build();
            await app.RunAsync(TestContext.Current.CancellationToken);
        }
        catch (DistributedApplicationException exception)
        {
            errors.Add(exception.Message);
        }

        return new PublishedChart(directory, chartDirectory, errors, ownsDirectory: into is null);
    }

    private sealed class PublishedChart(DirectoryInfo directory, string? chartDirectory, List<string> errors, bool ownsDirectory) : IDisposable
    {
        // With several compute environments, each writes its artifacts to a directory of its own.
        public string Path => chartDirectory is null ? directory.FullName : System.IO.Path.Combine(directory.FullName, chartDirectory);

        public IReadOnlyList<string> Errors
        {
            get
            {
                lock (errors)
                {
                    return [.. errors];
                }
            }
        }

        public IReadOnlyList<YamlMappingNode> All(string kind) =>
            [.. Documents().Where(d => d.Scalar("kind") == kind)];

        public YamlMappingNode Single(string kind, string? name = null) =>
            Assert.Single(All(kind), d => name is null || d.Scalar("metadata", "name") == name);

        public YamlNode Values(params string[] path)
        {
            var yaml = new YamlStream();
            yaml.Load(new StringReader(File.ReadAllText(System.IO.Path.Combine(Path, "values.yaml"))));
            return yaml.Documents[0].RootNode.Get(path);
        }

        public void Dispose()
        {
            if (ownsDirectory)
            {
                directory.Delete(recursive: true);
            }
        }

        private IEnumerable<YamlMappingNode> Documents()
        {
            var templates = System.IO.Path.Combine(Path, "templates");

            if (!Directory.Exists(templates))
            {
                yield break;
            }

            foreach (var file in Directory.EnumerateFiles(templates, "*.yaml", SearchOption.AllDirectories))
            {
                var yaml = new YamlStream();
                yaml.Load(new StringReader(File.ReadAllText(file)));

                foreach (var document in yaml.Documents)
                {
                    if (document.RootNode is YamlMappingNode mapping)
                    {
                        yield return mapping;
                    }
                }
            }
        }
    }

    private sealed class ErrorCollector(List<string> errors, ITestOutputHelper output) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Error)
            {
                return;
            }

            var text = $"{formatter(state, exception)} {exception}";

            lock (errors)
            {
                errors.Add(text);
            }

            output.WriteLine(text);
        }

        public void Dispose()
        {
        }
    }
}

internal static class YamlNavigation
{
    public static YamlNode? Find(this YamlNode node, params string[] path)
    {
        var current = node;

        foreach (var key in path)
        {
            if (current is not YamlMappingNode mapping || !mapping.Children.TryGetValue(new YamlScalarNode(key), out var child))
            {
                return null;
            }

            current = child;
        }

        return current;
    }

    public static YamlNode Get(this YamlNode node, params string[] path) =>
        node.Find(path) ?? throw new KeyNotFoundException(string.Join('.', path));

    public static YamlSequenceNode Items(this YamlNode node, params string[] path) => (YamlSequenceNode)node.Get(path);

    public static string? Scalar(this YamlNode node, params string[] path) => (node.Find(path) as YamlScalarNode)?.Value;
}
