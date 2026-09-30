using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Aspire.Hosting.RavenDB.Tests;
using CommunityToolkit.Aspire.Testing;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Kubernetes.Tests;

/// <summary>
/// bootstrap.sh, run as the Job runs it: bash in the RavenDB image, with the admin certificate and the
/// ServiceAccount mounted. The stand-in for curl plays both the cluster and the Kubernetes API.
/// </summary>
[RequiresDocker]
public sealed class BootstrapScriptTests : IDisposable
{
    private const string Leader = "https://a.shop.test";
    private const string Kubernetes = "https://kubernetes.default.svc/api/v1/namespaces/shop";
    private const string ServiceAccount = "/var/run/secrets/kubernetes.io/serviceaccount";

    private readonly ShellScriptHarness _harness = new ShellScriptHarness()
        .Mount($"{ServiceAccount}/namespace", "shop")
        .Mount($"{ServiceAccount}/token", "token")
        .Mount($"{ServiceAccount}/ca.crt", "ca")
        .Mount("/ravendb/admin/client.pfx", CreateCertificate("admin").Pfx)
        .Mount("/ravendb/bootstrap/bootstrap.sh", RavenDBClusterPublishing.Script)
        .Respond("GET", $"{Kubernetes}/serviceaccounts/ravendb-bootstrap", 200, """{"metadata":{"uid":"owner-uid"}}""")
        .Respond("GET", $"{Leader}/cluster/topology", 200, """{"Topology":{"Members":{"A":"https://a.shop.test","B":"https://b.shop.test"}}}""");

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task NewApplicationGetsACertificateForItsDatabasesInASecretTheChartOwns()
    {
        _harness
            .Respond("PUT", $"{Leader}/admin/databases?name=orders&replicationFactor=2", 201)
            .Respond("PUT", $"{Leader}/admin/certificates", 201)
            .Respond("GET", $"{Leader}/admin/certificates?start=0&*", 200, Certificates(("0123", "aspire.shop.worker"), ("4567", "orders-team")))
            .Respond("DELETE", $"{Leader}/admin/certificates?thumbprint=*", 204)
            .Respond("POST", $"{Kubernetes}/secrets", 201);

        var result = await Run();

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal("""{"DatabaseName":"orders"}""", Single("PUT", "/admin/databases").Body);

        var registered = Body(Single("PUT", "/admin/certificates"));
        Assert.Equal("aspire.shop.api", registered["Name"]!.GetValue<string>());
        Assert.Equal("ValidUser", registered["SecurityClearance"]!.GetValue<string>());
        Assert.Equal("""{"orders":"ReadWrite"}""", registered["Permissions"]!.ToJsonString());

        // The Secret holds the registered certificate, and the chart's ServiceAccount owns it.
        var secret = Body(Single("POST", "/secrets"));
        Assert.Equal("api", secret["metadata"]!["name"]!.GetValue<string>());
        Assert.Equal("owner-uid", secret["metadata"]!["ownerReferences"]![0]!["uid"]!.GetValue<string>());
        using var stored = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(secret["data"]!["client.pfx"]!.GetValue<string>()), "");
        Assert.Equal(registered["Certificate"]!.GetValue<string>(), Convert.ToBase64String(stored.RawData));
        Assert.True(stored.HasPrivateKey);

        // 'worker' is gone; certificates outside the prefix are someone else's.
        Assert.Equal($"{Leader}/admin/certificates?thumbprint=0123", Assert.Single(_harness.Requests, r => r.Method == "DELETE").Url);
    }

    [Fact]
    public async Task ExistingSecretKeepsItsCertificateAndGetsTheCurrentAccess()
    {
        var (pfx, thumbprint) = CreateCertificate("api");
        _harness
            .Respond("GET", $"{Leader}/databases?name=orders", 200)
            .Respond("GET", $"{Kubernetes}/secrets/api", 200, Secret("owner-uid", pfx))
            .Respond("GET", $"{Leader}/admin/certificates?thumbprint={thumbprint}", 200)
            .Respond("POST", $"{Leader}/admin/certificates/edit", 200)
            .Respond("GET", $"{Leader}/admin/certificates?start=0&*", 200, Certificates((thumbprint, "aspire.shop.api"), ("0123", "aspire.shop.api")))
            .Respond("DELETE", $"{Leader}/admin/certificates?thumbprint=*", 204);

        var result = await Run();

        Assert.True(result.ExitCode == 0, result.ToString());
        var edited = Body(Single("POST", "/admin/certificates/edit"));
        Assert.Equal(thumbprint, edited["Thumbprint"]!.GetValue<string>());
        Assert.Equal("""{"orders":"ReadWrite"}""", edited["Permissions"]!.ToJsonString());

        // An older certificate with the application's name is revoked; nothing is created.
        Assert.Equal($"{Leader}/admin/certificates?thumbprint=0123", Assert.Single(_harness.Requests, r => r.Method == "DELETE").Url);
        Assert.DoesNotContain(_harness.Requests, r => r.Method == "PUT" || r.Url.EndsWith("/secrets", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SecretTheChartDoesNotOwnIsNotTakenOver()
    {
        _harness
            .Respond("GET", $"{Leader}/databases?name=orders", 200)
            .Respond("GET", $"{Kubernetes}/secrets/api", 200, Secret("someone-else", CreateCertificate("api").Pfx));

        var result = await Run();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Secret 'api' was not created by this bootstrap", result.Output);
        Assert.DoesNotContain(_harness.Requests, r => r.Url.Contains("/admin/certificates", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DatabaseThatCannotBeCreatedFailsTheJob()
    {
        _harness.Respond("PUT", $"{Leader}/admin/databases?*", 500, "boom");

        var result = await Run();

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Creating database 'orders' failed: HTTP 500 boom", result.Output);
        Assert.DoesNotContain(_harness.Requests, r => r.Url.Contains("/secrets", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DepartedApplicationsAreFoundOnEveryPage()
    {
        var full = Enumerable.Range(0, 1024).Select(i => (i.ToString("X40", System.Globalization.CultureInfo.InvariantCulture), $"team-{i}")).ToArray();
        _harness
            .Respond("GET", $"{Leader}/databases?name=orders", 200)
            .Respond("PUT", $"{Leader}/admin/certificates", 201)
            .Respond("GET", $"{Leader}/admin/certificates?start=0&*", 200, Certificates(full))
            .Respond("GET", $"{Leader}/admin/certificates?start=1024&*", 200, Certificates(("0123", "aspire.shop.worker")))
            .Respond("DELETE", $"{Leader}/admin/certificates?thumbprint=*", 204)
            .Respond("POST", $"{Kubernetes}/secrets", 201);

        var result = await Run();

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal($"{Leader}/admin/certificates?thumbprint=0123", Assert.Single(_harness.Requests, r => r.Method == "DELETE").Url);
    }

    private Task<ShellScriptResult> Run() =>
        _harness.RunAsync(
            new Dictionary<string, string>
            {
                ["RAVENDB_URLS"] = "https://a.shop.test https://b.shop.test",
                ["RAVENDB_DATABASES"] = "orders",
                ["RAVENDB_REPLICATION_FACTOR"] = "2",
                ["RAVENDB_APPLICATIONS"] = "api=orders",
                ["RAVENDB_SECRET_OWNER"] = "ravendb-bootstrap",
            },
            "/bin/bash",
            "/ravendb/bootstrap/bootstrap.sh");

    private StubRequest Single(string method, string path) =>
        Assert.Single(_harness.Requests, r => r.Method == method && new Uri(r.Url).AbsolutePath.EndsWith(path, StringComparison.Ordinal));

    private static JsonNode Body(StubRequest request) => JsonNode.Parse(request.Body)!;

    private static string Certificates(params (string Thumbprint, string Name)[] certificates) =>
        JsonSerializer.Serialize(new { Results = certificates.Select(c => new { c.Thumbprint, c.Name }) });

    private static string Secret(string ownerUid, byte[] pfx) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["metadata"] = new { name = "api", ownerReferences = new[] { new { uid = ownerUid } } },
            ["data"] = new Dictionary<string, string> { ["client.pfx"] = Convert.ToBase64String(pfx) },
        });

    private static (byte[] Pfx, string Thumbprint) CreateCertificate(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        // Without a password, as the script writes them.
        return (certificate.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, ""), certificate.Thumbprint);
    }
}
