using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Cloud;

/// <summary>Creates <see cref="RavenDBCloudApiClient"/> instances; replaced in tests.</summary>
internal interface IRavenDBCloudApiClientFactory
{
    RavenDBCloudApiClient Create(string endpoint, string apiKey);
}

internal sealed class RavenDBCloudApiClientFactory : IRavenDBCloudApiClientFactory
{
    // No redirects: HttpClient would send the X-Api-Key header, which has account-owner rights, to wherever they point.
    public RavenDBCloudApiClient Create(string endpoint, string apiKey) =>
        new(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }), endpoint, apiKey, ownsHttpClient: true);
}

/// <summary>
/// Client for the RavenDB Cloud API v1 (<c>https://api.cloud.ravendb.net/api/v1/swagger.json</c>). Authentication is
/// an account-level API key sent in the <c>X-Api-Key</c> header.
/// </summary>
internal sealed class RavenDBCloudApiClient : IDisposable
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public RavenDBCloudApiClient(HttpClient http, string endpoint, string apiKey, bool ownsHttpClient = false)
    {
        _http = http;
        _ownsHttpClient = ownsHttpClient;

        _http.BaseAddress = new Uri(endpoint.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // The Cloud API's gateway answers requests without a User-Agent, which HttpClient does not send, with 403.
        _http.DefaultRequestHeaders.UserAgent.Add(UserAgent);
    }

    /// <summary>Delay before the first retry, doubled for each further one; shortened in tests.</summary>
    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    private const int MaxAttempts = 5;

    internal static ProductInfoHeaderValue UserAgent { get; } = new(
        "CommunityToolkit.Aspire.Hosting.RavenDB.Cloud",
        typeof(RavenDBCloudApiClient).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

    /// <summary>GET /api/v1/products/list.</summary>
    public async Task<IReadOnlyList<ProductListItem>> ListProductsAsync(CancellationToken cancellationToken)
    {
        using var response = await GetAsync("api/v1/products/list", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "list products", cancellationToken).ConfigureAwait(false);

        var list = await response.Content.ReadFromJsonAsync<ProductListResponse>(s_json, cancellationToken).ConfigureAwait(false);

        return list?.Items ?? [];
    }

    /// <summary>GET /api/v1/products/details/{id}; <see langword="null"/> when the product is gone.</summary>
    public async Task<ProductDetails?> GetProductAsync(string productId, CancellationToken cancellationToken)
    {
        using var response = await GetAsync($"api/v1/products/details/{Uri.EscapeDataString(productId)}", cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return null;
        }

        await EnsureSuccessAsync(response, "get product details", cancellationToken).ConfigureAwait(false);

        return await response.Content.ReadFromJsonAsync<ProductDetails>(s_json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>POST /api/v1/products/create; returns the new product id.</summary>
    public async Task<string> CreateProductAsync(ProductCreateRequest request, CancellationToken cancellationToken)
    {
        // Not retried on a server error: the product may have been created, and the next deployment finds it by name.
        using var response = await SendAsync(() => Post("api/v1/products/create", request), idempotent: false, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "create product", cancellationToken).ConfigureAwait(false);

        var created = await response.Content.ReadFromJsonAsync<ProductCreatedResponse>(s_json, cancellationToken).ConfigureAwait(false);

        return created?.ProductId ?? throw new InvalidOperationException("RavenDB Cloud did not return a product id.");
    }

    /// <summary>GET /api/v1/products/security/certificate/{id}: the client certificate bundle.</summary>
    public async Task<byte[]> GetClientCertificateAsync(string productId, CancellationToken cancellationToken)
    {
        using var response = await GetAsync($"api/v1/products/security/certificate/{Uri.EscapeDataString(productId)}", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "download the client certificate", cancellationToken).ConfigureAwait(false);

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>POST /api/v1/products/terminate/{id}. A product that is already gone is not an error.</summary>
    public async Task TerminateProductAsync(string productId, CancellationToken cancellationToken)
    {
        // Terminating twice changes nothing: a product already gone answers 404.
        using var response = await SendAsync(
            () => Post($"api/v1/products/terminate/{Uri.EscapeDataString(productId)}", new TerminateProductRequest(HideInPortal: false)),
            idempotent: true,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return;
        }

        await EnsureSuccessAsync(response, "terminate the product", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>GET /api/v1/metadata/instance-types/{provider}/{region}.</summary>
    public async Task<IReadOnlyList<InstanceTypeItem>> GetInstanceTypesAsync(string provider, string region, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(
            $"api/v1/metadata/instance-types/{Uri.EscapeDataString(provider)}/{Uri.EscapeDataString(region)}",
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "list instance types", cancellationToken).ConfigureAwait(false);

        var types = await response.Content.ReadFromJsonAsync<InstanceTypesResponse>(s_json, cancellationToken).ConfigureAwait(false);

        return types?.InstanceTypes ?? [];
    }

    /// <summary>GET /api/v1/metadata/release-channels.</summary>
    public async Task<ReleaseChannelsResponse> GetReleaseChannelsAsync(CancellationToken cancellationToken)
    {
        using var response = await GetAsync("api/v1/metadata/release-channels", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "list release channels", cancellationToken).ConfigureAwait(false);

        return await response.Content.ReadFromJsonAsync<ReleaseChannelsResponse>(s_json, cancellationToken).ConfigureAwait(false)
            ?? new ReleaseChannelsResponse(null, null);
    }

    private Task<HttpResponseMessage> GetAsync(string path, CancellationToken cancellationToken) =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Get, path), idempotent: true, cancellationToken);

    private static HttpRequestMessage Post<T>(string path, T body) =>
        new(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: s_json) };

    /// <summary>
    /// The API accepts about one request per second and answers 429 beyond that. A 429 was not processed, so any
    /// request is sent again; a server error or a lost connection only when sending it again changes nothing.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> request, bool idempotent, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;

            try
            {
                using var message = request();
                response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (idempotent && attempt < MaxAttempts)
            {
                await Task.Delay(RetryDelay * Math.Pow(2, attempt - 1), cancellationToken).ConfigureAwait(false);
                continue;
            }

            var retry = response.StatusCode == HttpStatusCode.TooManyRequests || (idempotent && (int)response.StatusCode >= 500);

            if (!retry || attempt == MaxAttempts)
            {
                return response;
            }

            var delay = response.Headers.RetryAfter?.Delta ?? RetryDelay * Math.Pow(2, attempt - 1);
            response.Dispose();
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var hint = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => " Check the RavenDB Cloud API key.",

            // The API's answer when the account already holds its quota of paid products (three by default).
            HttpStatusCode.PreconditionFailed =>
                " The account has reached its quota of paid products: terminate one in the RavenDB Cloud portal, or ask support to raise the quota.",
            _ => string.Empty,
        };

        throw new InvalidOperationException(
            $"RavenDB Cloud API failed to {operation}: {(int)response.StatusCode} {response.ReasonPhrase}.{hint} {body}".TrimEnd());
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}

internal sealed record ProductListResponse(IReadOnlyList<ProductListItem>? Items);

internal sealed record ProductListItem(string? Id, string? Name);

internal sealed record ProductCreateRequest(
    string CloudProvider,
    string InstanceTypeName,
    string DisplayName,
    string ReleaseChannel,
    string SubdomainName,
    string Tier,
    string Region,
    int DiskSize,
    string StorageTypeName,
    IReadOnlyList<string> AllowedIps);

internal sealed record ProductCreatedResponse(string? ProductId);

internal sealed record ProductDetails(
    string? Status,
    IReadOnlyList<string>? Dns,
    IReadOnlyList<string>? NodeTags);

internal sealed record TerminateProductRequest(bool HideInPortal);

internal sealed record InstanceTypesResponse(IReadOnlyList<InstanceTypeItem>? InstanceTypes);

internal sealed record InstanceTypeItem(string? Name, string? Tier, InstanceTypeParameters? Parameters);

internal sealed record InstanceTypeParameters(int? VirtualCpus, double? Ram, IReadOnlyList<int>? AvailableDiskSizes);

internal sealed record ReleaseChannelsResponse(string? DefaultReleaseChannel, IReadOnlyList<ReleaseChannelItem>? ReleaseChannels);

internal sealed record ReleaseChannelItem(string? Name);

/// <summary>Values of <c>ProductStatus</c>.</summary>
internal static class ProductStatus
{
    public const string Active = "Active";
    public const string Terminating = "Terminating";
    public const string Terminated = "Terminated";
    public const string Error = "Error";
    public const string AwaitingPayment = "AwaitingPayment";
}
