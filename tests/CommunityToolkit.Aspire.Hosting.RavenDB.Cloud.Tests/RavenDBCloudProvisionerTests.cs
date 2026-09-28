using Aspire.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Cloud.Tests;

public class RavenDBCloudProvisionerTests
{
    private readonly FakeRavenDBCloudApi _api = new();
    private readonly InMemoryDeploymentStateManager _state = new();

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

        Assert.Equal("https://a.ravendb-production.development.run", deployment.Endpoint.Url);
        Assert.All(_api.ApiKeys, key => Assert.Equal("test-api-key", key));

        var section = _state[deployment.StateSectionName]!;
        Assert.Equal(deployment.ProductId, section["productId"]!.GetValue<string>());
        Assert.True(section["createdByDeployment"]!.GetValue<bool>());
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

    private RavenDBCloudDeployment CreateDeployment(Action<RavenDBCloudOptions>? configure = null)
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRavenDB("ravendb");
        var apiKey = builder.AddParameter("ravendb-cloud-api-key", secret: true);

        var options = new RavenDBCloudOptions();
        configure?.Invoke(options);

        return new RavenDBCloudDeployment(server.Resource, apiKey.Resource, options, "Production")
        {
            PollInterval = TimeSpan.FromMilliseconds(1),
        };
    }

    private async Task Provision(RavenDBCloudDeployment deployment)
    {
        using var client = _api.Create("https://api.cloud.ravendb.net", "test-api-key");
        await new RavenDBCloudProvisioner(client, _state, NullLogger.Instance).ProvisionAsync(deployment, TestContext.Current.CancellationToken);
    }

    private async Task Destroy(RavenDBCloudDeployment deployment)
    {
        using var client = _api.Create("https://api.cloud.ravendb.net", "test-api-key");
        await new RavenDBCloudProvisioner(client, _state, NullLogger.Instance).DestroyAsync(deployment, TestContext.Current.CancellationToken);
    }
}
