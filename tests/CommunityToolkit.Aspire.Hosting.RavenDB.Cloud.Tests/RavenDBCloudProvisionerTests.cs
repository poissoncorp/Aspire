using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.ServerWide.Operations.Certificates;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Cloud.Tests;

public sealed class RavenDBCloudProvisionerTests : IDisposable
{
    private readonly FakeRavenDBCloudApi _api = new();
    private readonly FakeRavenDBServer _server = new();
    private readonly InMemoryDeploymentStateManager _state = new();
    private readonly DirectoryInfo _output = Directory.CreateTempSubdirectory(".ravendb-cloud-certificates");

    public void Dispose() => _output.Delete(recursive: true);

    [Fact]
    public async Task CreatesAMissingProductWithDefaultsResolvedFromMetadata()
    {
        var deployment = CreateDeployment(o => o.WithAllowedIps("203.0.113.0/24"));

        await Provision(deployment);

        var request = Assert.Single(_api.CreateRequests);
        Assert.Equal("ravendb-production", request["displayName"]!.GetValue<string>());
        Assert.Equal("Aws", request["cloudProvider"]!.GetValue<string>());
        Assert.Equal("Development", request["tier"]!.GetValue<string>());
        Assert.Equal("DV10", request["instanceTypeName"]!.GetValue<string>());
        Assert.Equal(10, request["diskSize"]!.GetValue<int>());
        Assert.Equal("Stable", request["releaseChannel"]!.GetValue<string>());
        Assert.Equal("SsdStandard", request["storageTypeName"]!.GetValue<string>());
        Assert.Equal("203.0.113.0/24", request["allowedIps"]![0]!.GetValue<string>());
        Assert.Equal(RavenDBCloudDeployment.DeriveSubdomain("ravendb-production"), request["subdomainName"]!.GetValue<string>());

        Assert.Equal("https://a.ravendb-production.development.run", deployment.Endpoint.Url);
        Assert.All(_api.ApiKeys, key => Assert.Equal("test-api-key", key));

        var section = _state[deployment.StateSectionName]!;
        Assert.Equal(deployment.ProductId, section["productId"]!.GetValue<string>());
        Assert.True(section["createdByDeployment"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CreatesTheProductWithTheGivenSubdomain()
    {
        var deployment = CreateDeployment(o =>
        {
            o.Subdomain = "shop";
            o.WithAllowedIps("203.0.113.0/24");
        });

        await Provision(deployment);

        Assert.Equal("shop", Assert.Single(_api.CreateRequests)["subdomainName"]!.GetValue<string>());
    }

    [Fact]
    public async Task AdoptsAProductWithTheSameNameInsteadOfCreatingASecondOne()
    {
        var existing = _api.AddProduct("ravendb-production");
        existing.NodeTags = ["A", "B", "C"];
        var deployment = CreateDeployment(o => o.WithAllowedIps("203.0.113.0/24"));

        await Provision(deployment);

        Assert.Empty(_api.CreateRequests);
        Assert.Equal(existing.Id, deployment.ProductId);
        Assert.Equal(3, deployment.NodeCount);
        Assert.False(_state[deployment.StateSectionName]!["createdByDeployment"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ReusesTheProductRecordedInDeploymentState()
    {
        var deployment = CreateDeployment(o => o.WithAllowedIps("203.0.113.0/24"));
        await Provision(deployment);

        var second = CreateDeployment(o => o.WithAllowedIps("203.0.113.0/24"));
        await Provision(second);

        Assert.Single(_api.CreateRequests);
        Assert.Equal(deployment.ProductId, second.ProductId);
        Assert.True(_state[deployment.StateSectionName]!["createdByDeployment"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ExistingProductThatIsMissingFailsInsteadOfCreatingOne()
    {
        var deployment = CreateDeployment(o => o.AsExisting("orders-production"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Provision(deployment));

        Assert.Contains("No RavenDB Cloud product named 'orders-production'", exception.Message);
        Assert.Empty(_api.CreateRequests);
    }

    [Fact]
    public async Task ExistingProductIsConnectedTo()
    {
        var product = _api.AddProduct("orders-production");
        var deployment = CreateDeployment(o => o.AsExisting("orders-production"));

        await Provision(deployment);

        Assert.Equal(product.Id, deployment.ProductId);
        Assert.Equal("https://a.orders-production.development.run", deployment.Endpoint.Url);
    }

    [Fact]
    public async Task CreatingWithoutAllowedIpsFailsWithAnExplanation()
    {
        var deployment = CreateDeployment();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Provision(deployment));

        Assert.Contains("needs at least one allowed IP range", exception.Message);
        Assert.Empty(_api.CreateRequests);
    }

    [Fact]
    public async Task TwoProductsWithTheSameNameAreRejected()
    {
        _api.AddProduct("ravendb-production");
        _api.AddProduct("ravendb-production");
        var deployment = CreateDeployment(o => o.WithAllowedIps("203.0.113.0/24"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Provision(deployment));

        Assert.Contains("2 RavenDB Cloud products named 'ravendb-production'", exception.Message);
    }

    [Fact]
    public async Task ProductAwaitingPaymentFails()
    {
        _api.AddProduct("ravendb-production", status: "AwaitingPayment");
        var deployment = CreateDeployment();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Provision(deployment));

        Assert.Contains("awaiting payment", exception.Message);
    }

    [Fact]
    public async Task DestroyTerminatesOnlyAProductThisDeploymentCreatedAndOnlyWhenAskedTo()
    {
        var keep = CreateDeployment(o => o.WithAllowedIps("203.0.113.0/24"));
        await Provision(keep);
        await Destroy(keep);
        Assert.Empty(_api.TerminatedProductIds);

        var terminate = CreateDeployment(o =>
        {
            o.WithAllowedIps("203.0.113.0/24");
            o.TerminateOnDestroy = true;
        });
        await Destroy(terminate);

        Assert.Equal(keep.ProductId, Assert.Single(_api.TerminatedProductIds));
        Assert.Null(_state[terminate.StateSectionName]);
    }

    [Fact]
    public async Task DestroyLeavesAnAdoptedProductRunning()
    {
        _api.AddProduct("ravendb-production");
        var deployment = CreateDeployment(o => o.TerminateOnDestroy = true);
        await Provision(deployment);

        await Destroy(deployment);

        Assert.Empty(_api.TerminatedProductIds);
    }

    [Fact]
    public async Task DestroyNeverTouchesAnExistingProduct()
    {
        _api.AddProduct("orders-production");
        var deployment = CreateDeployment(o =>
        {
            o.AsExisting("orders-production");
            o.TerminateOnDestroy = true;
        });
        await Provision(deployment);

        await Destroy(deployment);

        Assert.Empty(_api.TerminatedProductIds);
    }

    [Fact]
    public void CertificateBundleYieldsThePfx()
    {
        var pfx = new byte[] { 1, 2, 3 };
        using var zip = new MemoryStream();

        using (var archive = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var pem = archive.CreateEntry("client.crt").Open())
            {
                pem.Write([9, 9]);
            }

            using var entry = archive.CreateEntry("client.pfx").Open();
            entry.Write(pfx);
        }

        Assert.Equal(pfx, RavenDBCloudProvisioner.ExtractPfx(zip.ToArray()));
        Assert.Equal(pfx, RavenDBCloudProvisioner.ExtractPfx(pfx));
        Assert.Null(RavenDBCloudProvisioner.ExtractPfx([]));
    }

    [Fact]
    public async Task CreatesTheDeclaredDatabasesWithTheAdminCertificate()
    {
        _server.Databases.Add("reports");
        var deployment = CreateDeployment(
            o => o.WithAllowedIps("203.0.113.0/24"),
            server =>
            {
                server.AddDatabase("orders", ensureCreated: true);
                server.AddDatabase("reports", ensureCreated: true);
            });
        await Provision(deployment);

        await Run(p => p.EnsureDatabasesAsync(deployment, TestContext.Current.CancellationToken));

        Assert.Equal(["orders", "reports"], _server.Databases.Order());
        var connection = Assert.Single(_server.Connections);
        Assert.Equal("https://a.ravendb-production.development.run", connection.Url);
        Assert.Equal(RavenDBCloudProvisioner.GetThumbprint(_api.AdminCertificate), connection.CertificateThumbprint);
    }

    [Fact]
    public async Task ExistingProductGetsNoDatabases()
    {
        _api.AddProduct("orders-production");
        var deployment = CreateDeployment(o => o.AsExisting("orders-production"), server => server.AddDatabase("orders", ensureCreated: true));
        await Provision(deployment);

        await Run(p => p.EnsureDatabasesAsync(deployment, TestContext.Current.CancellationToken));

        Assert.Empty(_server.Databases);
        Assert.Empty(_server.Connections);
    }

    [Fact]
    public async Task IssuesEachApplicationACertificateForItsDatabasesOnly()
    {
        var deployment = await ProvisionedDeployment();

        await IssueCertificates(deployment, Request("api", "orders"), Request("worker", "orders", "reports"));

        var api = CertificateOnDisk("api");
        Assert.Equal("aspire.test.production.api", api.Name);
        Assert.Equal(["orders"], api.Permissions.Keys);
        Assert.Equal(["orders", "reports"], CertificateOnDisk("worker").Permissions.Keys.Order());
        Assert.All(_server.Certificates.Values.SelectMany(c => c.Permissions.Values), access => Assert.Equal(DatabaseAccess.ReadWrite, access));

        Assert.Equal("*", File.ReadAllText(Path.Combine(_output.FullName, "ravendb-certs", ".gitignore")).Trim());
        Assert.Equal(ThumbprintOnDisk("api"), _state[deployment.StateSectionName]!["clientCertificates"]!["api"]!["thumbprint"]!.GetValue<string>());

        // One download of the admin certificate serves every application.
        Assert.Single(_api.CertificateDownloads);
    }

    [Fact]
    public async Task RedeployKeepsTheCertificates()
    {
        await IssueCertificates(await ProvisionedDeployment(), Request("api", "orders"));
        var issued = ThumbprintOnDisk("api");

        await IssueCertificates(await ProvisionedDeployment(), Request("api", "orders"));

        Assert.Equal(issued, ThumbprintOnDisk("api"));
        Assert.Single(_server.Certificates);
        Assert.Empty(_server.EditedThumbprints);
        Assert.Empty(_server.DeletedThumbprints);
    }

    [Fact]
    public async Task ChangedAccessIsUpdatedOnTheSameCertificate()
    {
        await IssueCertificates(await ProvisionedDeployment(), Request("api", "orders"));
        var issued = ThumbprintOnDisk("api");

        await IssueCertificates(await ProvisionedDeployment(), Request("api", "orders", "reports"));

        Assert.Equal(issued, ThumbprintOnDisk("api"));
        Assert.Equal(issued, Assert.Single(_server.EditedThumbprints));
        Assert.Equal(["orders", "reports"], CertificateOnDisk("api").Permissions.Keys.Order());
    }

    [Fact]
    public async Task MissingFileGetsANewCertificateAndTheOldOneIsRevoked()
    {
        await IssueCertificates(await ProvisionedDeployment(), Request("api", "orders"));
        var issued = ThumbprintOnDisk("api");
        File.Delete(CertificatePath("api"));

        await IssueCertificates(await ProvisionedDeployment(), Request("api", "orders"));

        Assert.NotEqual(issued, ThumbprintOnDisk("api"));
        Assert.Equal(issued, Assert.Single(_server.DeletedThumbprints));
        Assert.Single(_server.Certificates);
    }

    [Fact]
    public async Task CertificateRemovedFromTheServerIsIssuedAgain()
    {
        await IssueCertificates(await ProvisionedDeployment(), Request("api", "orders"));
        var issued = ThumbprintOnDisk("api");
        _server.Certificates.Clear();

        await IssueCertificates(await ProvisionedDeployment(), Request("api", "orders"));

        Assert.NotEqual(issued, ThumbprintOnDisk("api"));
        Assert.Equal(["orders"], CertificateOnDisk("api").Permissions.Keys);
        Assert.Empty(_server.DeletedThumbprints);
    }

    [Fact]
    public async Task ApplicationsNoLongerDeployedLoseTheirCertificates()
    {
        await IssueCertificates(await ProvisionedDeployment(), Request("api", "orders"), Request("worker", "orders"));
        var worker = ThumbprintOnDisk("worker");

        var deployment = await ProvisionedDeployment();
        await IssueCertificates(deployment, Request("api", "orders"));

        Assert.Equal(worker, Assert.Single(_server.DeletedThumbprints));
        Assert.False(File.Exists(CertificatePath("worker")));
        Assert.Null(_state[deployment.StateSectionName]!["clientCertificates"]!["worker"]);
    }

    [Fact]
    public async Task CertificatesNeedAnHttpsProduct()
    {
        _api.AddProduct("ravendb-production").Dns = ["http://a.ravendb-production.development.run"];
        var deployment = await ProvisionedDeployment();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => IssueCertificates(deployment, Request("api", "orders")));

        Assert.Contains("not served over HTTPS", exception.Message);
    }

    [Fact]
    public async Task DestroyRevokesTheCertificatesOfAProductThatKeepsRunning()
    {
        await IssueCertificates(await ProvisionedDeployment(), Request("api", "orders"), Request("worker", "orders"));
        var issued = _server.Certificates.Keys.ToList();

        // A new process: the product is known from the deployment state only.
        var deployment = CreateDeployment(o => o.WithAllowedIps("203.0.113.0/24"));
        await Destroy(deployment);

        Assert.Equal(issued.Order(), _server.DeletedThumbprints.Order());
        Assert.False(File.Exists(CertificatePath("api")));
        Assert.Null(_state[deployment.StateSectionName]!["clientCertificates"]);
        Assert.Single(_api.Products);
    }

    [Fact]
    public async Task DestroyThatTerminatesTheProductLeavesTheCertificatesToIt()
    {
        await IssueCertificates(await ProvisionedDeployment(), Request("api", "orders"));

        await Destroy(CreateDeployment(o =>
        {
            o.WithAllowedIps("203.0.113.0/24");
            o.TerminateOnDestroy = true;
        }));

        Assert.Empty(_server.DeletedThumbprints);
        Assert.Single(_api.TerminatedProductIds);
        Assert.False(File.Exists(CertificatePath("api")));
    }

    [Fact]
    public async Task DestroyOfAnExistingProductRevokesItsCertificatesOnly()
    {
        _api.AddProduct("orders-production");
        var deployment = CreateDeployment(o => o.AsExisting("orders-production"));
        await Provision(deployment);
        await IssueCertificates(deployment, Request("api", "orders"));
        var issued = ThumbprintOnDisk("api");

        await Destroy(CreateDeployment(o => o.AsExisting("orders-production")));

        Assert.Equal(issued, Assert.Single(_server.DeletedThumbprints));
        Assert.Empty(_api.TerminatedProductIds);
        Assert.Null(_state[deployment.StateSectionName]);
    }

    private async Task<RavenDBCloudDeployment> ProvisionedDeployment()
    {
        var deployment = CreateDeployment(o => o.WithAllowedIps("203.0.113.0/24"));
        await Provision(deployment);
        return deployment;
    }

    private ClientCertificateRequest Request(string consumer, params string[] databases) =>
        new(consumer, $"aspire.test.production.{consumer}", databases, CertificatePath(consumer));

    private string CertificatePath(string consumer) => Path.Combine(_output.FullName, "ravendb-certs", $"ravendb-{consumer}.pfx");

    private string ThumbprintOnDisk(string consumer) => RavenDBCloudProvisioner.GetThumbprint(File.ReadAllBytes(CertificatePath(consumer)));

    private FakeCertificate CertificateOnDisk(string consumer) => _server.Certificates[ThumbprintOnDisk(consumer)];

    private Task IssueCertificates(RavenDBCloudDeployment deployment, params ClientCertificateRequest[] requests) =>
        Run(p => p.EnsureClientCertificatesAsync(deployment, requests, TestContext.Current.CancellationToken));

    private RavenDBCloudDeployment CreateDeployment(
        Action<RavenDBCloudOptions>? configure = null,
        Action<IResourceBuilder<RavenDBServerResource>>? configureServer = null)
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRavenDB("ravendb");
        configureServer?.Invoke(server);
        var apiKey = builder.AddParameter("ravendb-cloud-api-key", secret: true);

        var options = new RavenDBCloudOptions();
        configure?.Invoke(options);

        return new RavenDBCloudDeployment(server.Resource, apiKey.Resource, options, "Production")
        {
            PollInterval = TimeSpan.FromMilliseconds(1),
        };
    }

    private Task Provision(RavenDBCloudDeployment deployment) =>
        Run(p => p.ProvisionAsync(deployment, TestContext.Current.CancellationToken));

    private Task Destroy(RavenDBCloudDeployment deployment) =>
        Run(p => p.DestroyAsync(deployment, TestContext.Current.CancellationToken));

    private async Task Run(Func<RavenDBCloudProvisioner, Task> action)
    {
        using var client = _api.Create("https://api.cloud.ravendb.net", "test-api-key");
        await action(new RavenDBCloudProvisioner(client, _state, _server, NullLogger.Instance));
    }
}
