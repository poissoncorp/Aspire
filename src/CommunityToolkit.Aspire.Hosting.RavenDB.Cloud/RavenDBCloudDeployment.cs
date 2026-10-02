using System.Security.Cryptography;
using System.Text;
using Aspire.Hosting.ApplicationModel;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Cloud;

/// <summary>
/// Attached to a <see cref="RavenDBServerResource"/> published to RavenDB Cloud: what to provision and, once the
/// deployment has run, where the product is.
/// </summary>
internal sealed class RavenDBCloudDeployment : IResourceAnnotation
{
    public RavenDBCloudDeployment(
        RavenDBServerResource server,
        ParameterResource apiKey,
        RavenDBCloudOptions options,
        string appHostName,
        string environmentName)
    {
        Server = server;
        ApiKey = apiKey;
        Options = options;
        AppHostName = appHostName;
        EnvironmentName = environmentName;

        // Named after the AppHost too: two AppHosts in one account, both with AddRavenDB("ravendb"), must not
        // share a product and its databases.
        ProductName = options.ProductName ?? $"{appHostName}-{server.Name}-{environmentName}".Replace('.', '-').ToLowerInvariant();
        Subdomain = options.Subdomain ?? DeriveSubdomain(ProductName);
        Endpoint = new RavenDBCloudEndpoint(server.Name);

        // Only AWS has a region to assume; elsewhere a guess would fail once the product is created.
        Region = options.Region ?? (options.Provider == RavenDBCloudProvider.Aws
            ? "us-east-1"
            : throw new ArgumentException(
                $"Set Region for RavenDB Cloud server '{server.Name}': there is no default region on {options.Provider}.",
                nameof(options)));
    }

    /// <summary>The longest subdomain RavenDB Cloud accepts.</summary>
    public const int MaxSubdomainLength = 13;

    public RavenDBServerResource Server { get; }

    public ParameterResource ApiKey { get; }

    public RavenDBCloudOptions Options { get; }

    /// <summary>The provider region the product is created in.</summary>
    public string Region { get; }

    /// <summary>The AppHost the product and the applications' certificates are named after.</summary>
    public string AppHostName { get; }

    /// <summary>The environment the product and the applications' certificates are named after.</summary>
    public string EnvironmentName { get; }

    /// <summary>Display name the product is looked up and created by.</summary>
    public string ProductName { get; }

    /// <summary>Subdomain a new product is created with.</summary>
    public string Subdomain { get; }

    /// <summary>
    /// The product name's letters, digits and dashes; when that is too long, its beginning and a hash of the whole
    /// name, so <c>ravendb-staging</c> and <c>ravendb-production</c> still get different subdomains.
    /// </summary>
    internal static string DeriveSubdomain(string productName)
    {
        var allowed = new string([.. productName.ToLowerInvariant().Where(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')]).Trim('-');

        if (allowed.Length is > 0 and <= MaxSubdomainLength)
        {
            return allowed;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(productName)))[..5].ToLowerInvariant();
        var prefix = allowed[..Math.Min(allowed.Length, MaxSubdomainLength - hash.Length - 1)].TrimEnd('-');

        return prefix.Length == 0 ? $"p{hash}" : $"{prefix}-{hash}";
    }

    /// <summary>Whether RavenDB Cloud accepts <paramref name="subdomain"/>.</summary>
    internal static bool IsValidSubdomain(string subdomain) =>
        subdomain.Length is > 0 and <= MaxSubdomainLength &&
        subdomain.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') &&
        !subdomain.StartsWith('-') &&
        !subdomain.EndsWith('-');

    /// <summary>The product URL, resolved by the provisioning step.</summary>
    public RavenDBCloudEndpoint Endpoint { get; }

    /// <summary>Id of the product, known once the provisioning step has run.</summary>
    public string? ProductId { get; set; }

    /// <summary>The product's admin certificate (pfx), downloaded by the first step that needs it.</summary>
    public byte[]? AdminCertificate { get; set; }

    /// <summary>Nodes in the product's cluster; used as the replication factor of new databases.</summary>
    public int NodeCount { get; set; } = 1;

    /// <summary>Delay between two status checks while a product is being created; shortened in tests.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Deployment state section holding the id of the product this deployment created.</summary>
    public string StateSectionName => $"ravendb-cloud.{Server.Name}";
}

/// <summary>
/// The product URL as a value consumers reference. It is unknown when the artifacts are written, so they carry a
/// placeholder (<c>RAVENDB_URL</c> in Docker Compose, a Bicep parameter in Azure Container Apps) that the
/// deployment fills in.
/// </summary>
internal sealed class RavenDBCloudEndpoint(string resourceName) : IValueProvider, IManifestExpressionProvider
{
    public string? Url { get; set; }

    public string ValueExpression => $"{{{resourceName}.url}}";

    public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default) => new(Url);
}
