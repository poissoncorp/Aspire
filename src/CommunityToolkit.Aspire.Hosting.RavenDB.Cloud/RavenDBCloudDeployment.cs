using Aspire.Hosting.ApplicationModel;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Cloud;

/// <summary>
/// Attached to a <see cref="RavenDBServerResource"/> published to RavenDB Cloud: what to provision and, once the
/// deployment has run, where the product is.
/// </summary>
internal sealed class RavenDBCloudDeployment : IResourceAnnotation
{
    public RavenDBCloudDeployment(RavenDBServerResource server, ParameterResource apiKey, RavenDBCloudOptions options, string environmentName)
    {
        Server = server;
        ApiKey = apiKey;
        Options = options;
        ProductName = options.ProductName ?? $"{server.Name}-{environmentName}".ToLowerInvariant();
        Endpoint = new RavenDBCloudEndpoint(server.Name);
    }

    public RavenDBServerResource Server { get; }

    public ParameterResource ApiKey { get; }

    public RavenDBCloudOptions Options { get; }

    /// <summary>Display name the product is looked up and created by.</summary>
    public string ProductName { get; }

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

    /// <summary>Deployment state section holding the product id and URL between deployments.</summary>
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
