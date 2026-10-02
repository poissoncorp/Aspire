using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Pipelines;
using Raven.Client.ServerWide.Operations.Certificates;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Cloud.Tests;

/// <summary>
/// An in-memory RavenDB Cloud API with the endpoints the integration calls, shaped after
/// https://api.cloud.ravendb.net/api/v1/swagger.json.
/// </summary>
internal sealed class FakeRavenDBCloudApi : HttpMessageHandler, IRavenDBCloudApiClientFactory
{
    private int _nextId;

    public ConcurrentDictionary<string, FakeProduct> Products { get; } = new();

    public List<JsonObject> CreateRequests { get; } = [];

    public List<string> TerminatedProductIds { get; } = [];

    public List<string> ApiKeys { get; } = [];

    public List<string> CertificateDownloads { get; } = [];

    /// <summary>The admin certificate every product hands out.</summary>
    public byte[] AdminCertificate { get; } = TestCertificates.CreatePfx("admin");

    /// <summary>How many status checks a new product reports "Creating" before it becomes "Active".</summary>
    public int CreatingPolls { get; set; } = 2;

    public FakeProduct AddProduct(string name, string status = "Active")
    {
        var id = $"product-{Interlocked.Increment(ref _nextId)}";
        // Host names without a scheme, as the real API reports them.
        var product = new FakeProduct(id, name) { Status = status, Dns = [$"a.{name}.development.run"] };
        Products[id] = product;
        return product;
    }

    /// <summary>Answers the next requests with these statuses before handling them, as a busy or failing API does.</summary>
    public ConcurrentQueue<HttpStatusCode> Failures { get; } = new();

    public List<string> Requests { get; } = [];

    /// <summary>Answers the next product creation with this status, once.</summary>
    public HttpStatusCode? FailCreation { get; set; }

    public RavenDBCloudApiClient Create(string endpoint, string apiKey) =>
        new(new HttpClient(this, disposeHandler: false), endpoint, apiKey) { RetryDelay = TimeSpan.FromMilliseconds(1) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (ApiKeys)
        {
            ApiKeys.Add(request.Headers.TryGetValues("X-Api-Key", out var keys) ? keys.Single() : "");
        }

        // As the real API's gateway does.
        if (request.Headers.UserAgent.Count == 0)
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden) { Content = new StringContent("<html>403 Forbidden</html>") };
        }

        var path = request.RequestUri!.AbsolutePath;

        lock (Requests)
        {
            Requests.Add($"{request.Method} {path}");
        }

        if (Failures.TryDequeue(out var failure))
        {
            return new HttpResponseMessage(failure);
        }

        if (request.Method == HttpMethod.Post && path == "/api/v1/products/create" && FailCreation is { } creationFailure)
        {
            FailCreation = null;
            return new HttpResponseMessage(creationFailure);
        }

        if (request.Method == HttpMethod.Get && path == "/api/v1/products/list")
        {
            return Json(new { items = Products.Values.Select(p => new { id = p.Id, name = p.Name }) });
        }

        if (request.Method == HttpMethod.Get && path.StartsWith("/api/v1/products/details/", StringComparison.Ordinal))
        {
            if (!Products.TryGetValue(path.Split('/').Last(), out var product))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (product.Status == "Creating" && ++product.Polls >= CreatingPolls)
            {
                product.Status = "Active";
            }

            return Json(new
            {
                id = product.Id,
                displayName = product.Name,
                status = product.Status,
                dns = product.Status == "Active" ? product.Dns : [],
                nodeTags = product.NodeTags,
            });
        }

        if (request.Method == HttpMethod.Post && path == "/api/v1/products/create")
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();

            lock (CreateRequests)
            {
                CreateRequests.Add(body);
            }

            // As the real API validates it: the OpenAPI document marks it optional, paid tiers require it.
            var subdomain = body["subdomainName"]?.GetValue<string>();

            if (string.IsNullOrEmpty(subdomain) && body["tier"]?.GetValue<string>() != "Free")
            {
                return Json(new { subdomainName = new[] { "Subdomain Name is required.", "Domain name cannot be empty." } }, HttpStatusCode.BadRequest);
            }

            if (Products.Values.Any(p => p.Subdomain == subdomain))
            {
                return Json(new { subdomainName = new[] { "Subdomain is already taken." } }, HttpStatusCode.BadRequest);
            }

            var product = AddProduct(body["displayName"]!.GetValue<string>(), status: "Creating");
            product.Subdomain = subdomain;
            return Json(new { productId = product.Id }, HttpStatusCode.Accepted);
        }

        if (request.Method == HttpMethod.Post && path.StartsWith("/api/v1/products/terminate/", StringComparison.Ordinal))
        {
            var id = path.Split('/').Last();

            lock (TerminatedProductIds)
            {
                TerminatedProductIds.Add(id);
            }

            // Like the real API, a terminated product stays in the list.
            if (!Products.TryGetValue(id, out var terminated))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            terminated.Status = "Terminated";
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }

        if (request.Method == HttpMethod.Get && path.StartsWith("/api/v1/metadata/instance-types/", StringComparison.Ordinal))
        {
            return Json(new
            {
                instanceTypes = new object[]
                {
                    new { name = "PR30", tier = "Production", parameters = new { virtualCpus = 4, ram = 16, availableDiskSizes = new[] { 128, 256 }, numberOfNodes = 3 } },
                    new { name = "DV20", tier = "Development", parameters = new { virtualCpus = 2, ram = 4, availableDiskSizes = new[] { 32, 64 }, numberOfNodes = 1 } },
                    new { name = "DV10", tier = "Development", parameters = new { virtualCpus = 1, ram = 2, availableDiskSizes = new[] { 20, 10 }, numberOfNodes = 1 } },
                },
            });
        }

        if (request.Method == HttpMethod.Get && path.StartsWith("/api/v1/products/security/certificate/", StringComparison.Ordinal))
        {
            lock (CertificateDownloads)
            {
                CertificateDownloads.Add(path.Split('/').Last());
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TestCertificates.Bundle("admin", AdminCertificate)) };
        }

        if (request.Method == HttpMethod.Get && path == "/api/v1/metadata/release-channels")
        {
            return Json(new { defaultReleaseChannel = "Stable", releaseChannels = new[] { new { name = "Stable" }, new { name = "Beta" } } });
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(value, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
}

internal sealed class FakeProduct(string id, string name)
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    public string? Subdomain { get; set; }

    public string Status { get; set; } = "Active";

    public string[] Dns { get; set; } = [];

    public string[] NodeTags { get; set; } = ["A"];

    public int Polls { get; set; }
}

/// <summary>
/// An in-memory RavenDB server with the certificate and database operations a deployment performs.
/// </summary>
internal sealed class FakeRavenDBServer : IRavenDBServerAdministrationFactory
{
    public HashSet<string> Databases { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, FakeCertificate> Certificates { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> DeletedThumbprints { get; } = [];

    public List<string> EditedThumbprints { get; } = [];

    /// <summary>URL and admin certificate thumbprint of every connection.</summary>
    public List<(string Url, string? CertificateThumbprint)> Connections { get; } = [];

    public IRavenDBServerAdministration Create(string url, X509Certificate2? certificate)
    {
        Connections.Add((url, certificate?.Thumbprint));
        certificate?.Dispose();

        return new Session(this);
    }

    private sealed class Session(FakeRavenDBServer server) : IRavenDBServerAdministration
    {
        public Task<bool> DatabaseExistsAsync(string database, CancellationToken cancellationToken) =>
            Task.FromResult(server.Databases.Contains(database));

        public Task<bool> CreateDatabaseAsync(string database, int replicationFactor, CancellationToken cancellationToken) =>
            Task.FromResult(server.Databases.Add(database));

        public Task<IReadOnlyList<RegisteredCertificate>> GetCertificatesAsync(string namePrefix, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RegisteredCertificate>>([.. server.Certificates
                .Where(c => c.Value.Name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
                .Select(c => new RegisteredCertificate(
                    c.Value.Name,
                    c.Key,
                    c.Value.Clearance,
                    new Dictionary<string, DatabaseAccess>(c.Value.Permissions, StringComparer.OrdinalIgnoreCase)))]);

        public Task<byte[]> CreateClientCertificateAsync(string name, IReadOnlyDictionary<string, DatabaseAccess> permissions, CancellationToken cancellationToken)
        {
            var pfx = TestCertificates.CreatePfx(name);
            server.Certificates[RavenDBCloudClientCertificates.GetThumbprint(pfx)] = new FakeCertificate(name, new Dictionary<string, DatabaseAccess>(permissions));

            return Task.FromResult(TestCertificates.Bundle(name, pfx));
        }

        public Task SetCertificatePermissionsAsync(string thumbprint, string name, IReadOnlyDictionary<string, DatabaseAccess> permissions, CancellationToken cancellationToken)
        {
            server.EditedThumbprints.Add(thumbprint);
            server.Certificates[thumbprint] = new FakeCertificate(name, new Dictionary<string, DatabaseAccess>(permissions));

            return Task.CompletedTask;
        }

        public Task DeleteCertificateAsync(string thumbprint, CancellationToken cancellationToken)
        {
            server.DeletedThumbprints.Add(thumbprint);
            server.Certificates.Remove(thumbprint);

            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}

internal sealed record FakeCertificate(
    string Name,
    Dictionary<string, DatabaseAccess> Permissions,
    SecurityClearance Clearance = SecurityClearance.ValidUser);

internal static class TestCertificates
{
    public static byte[] CreatePfx(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        return certificate.Export(X509ContentType.Pfx);
    }

    /// <summary>The zip RavenDB and RavenDB Cloud return: the pfx next to the PEM files.</summary>
    public static byte[] Bundle(string name, byte[] pfx)
    {
        using var zip = new MemoryStream();

        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var pem = archive.CreateEntry($"{name}.crt").Open())
            {
                pem.Write("-----BEGIN CERTIFICATE-----"u8);
            }

            using var entry = archive.CreateEntry($"{name}.pfx").Open();
            entry.Write(pfx);
        }

        return zip.ToArray();
    }
}

internal sealed class InMemoryDeploymentStateManager : IDeploymentStateManager
{
    private readonly Dictionary<string, (JsonObject Data, long Version)> _sections = new(StringComparer.Ordinal);

    public string? StateFilePath => null;

    public JsonObject? this[string sectionName] => _sections.TryGetValue(sectionName, out var section) ? section.Data : null;

    public Task<DeploymentStateSection> AcquireSectionAsync(string sectionName, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sections.TryGetValue(sectionName, out var section)
            ? new DeploymentStateSection(sectionName, (JsonObject)section.Data.DeepClone(), section.Version)
            : new DeploymentStateSection(sectionName, [], 0));

    public Task SaveSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default)
    {
        var current = _sections.TryGetValue(section.SectionName, out var existing) ? existing.Version : 0;

        if (current != section.Version)
        {
            throw new InvalidOperationException($"Concurrency conflict on '{section.SectionName}'.");
        }

        _sections[section.SectionName] = ((JsonObject)section.Data.DeepClone(), current + 1);
        section.Version = current + 1;

        return Task.CompletedTask;
    }

    public Task DeleteSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default)
    {
        _sections.Remove(section.SectionName);
        return Task.CompletedTask;
    }

    public Task ClearAllStateAsync(CancellationToken cancellationToken = default)
    {
        _sections.Clear();
        return Task.CompletedTask;
    }
}
