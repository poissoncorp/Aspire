using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Pipelines;

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

    /// <summary>How many status checks a new product reports "Creating" before it becomes "Active".</summary>
    public int CreatingPolls { get; set; } = 2;

    public FakeProduct AddProduct(string name, string status = "Active")
    {
        var id = $"product-{Interlocked.Increment(ref _nextId)}";
        var product = new FakeProduct(id, name) { Status = status, Dns = [$"https://a.{name}.development.run"] };
        Products[id] = product;
        return product;
    }

    public RavenDBCloudApiClient Create(string endpoint, string apiKey) =>
        new(new HttpClient(this, disposeHandler: false), endpoint, apiKey);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (ApiKeys)
        {
            ApiKeys.Add(request.Headers.TryGetValues("X-Api-Key", out var keys) ? keys.Single() : "");
        }

        var path = request.RequestUri!.AbsolutePath;

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

            var product = AddProduct(body["displayName"]!.GetValue<string>(), status: "Creating");
            return Json(new { productId = product.Id }, HttpStatusCode.Accepted);
        }

        if (request.Method == HttpMethod.Post && path.StartsWith("/api/v1/products/terminate/", StringComparison.Ordinal))
        {
            var id = path.Split('/').Last();

            lock (TerminatedProductIds)
            {
                TerminatedProductIds.Add(id);
            }

            return Products.TryRemove(id, out _)
                ? new HttpResponseMessage(HttpStatusCode.Accepted)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
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

    public string Status { get; set; } = "Active";

    public string[] Dns { get; set; } = [];

    public string[] NodeTags { get; set; } = ["A"];

    public int Polls { get; set; }
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
