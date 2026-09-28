using System.Security.Cryptography.X509Certificates;

#pragma warning disable ASPIREATS001 // AspireExport is experimental

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents a RavenDB container.
/// </summary>
[AspireExport(ExposeProperties = true)]
public class RavenDBServerResource(string name, bool isSecured) : ContainerResource(name), IResourceWithConnectionString
{
    /// <summary>
    /// Indicates whether the server connection is secured (HTTPS) or not (HTTP).
    /// </summary>
    internal bool IsSecured { get; } = isSecured;

    /// <summary>
    /// Gets the protocol used for the primary endpoint, based on the security setting ("http" or "https").
    /// </summary>
    internal string PrimaryEndpointName => IsSecured ? "https" : "http";
    /// <summary>
    /// Gets the name for the TCP Endpoint
    /// </summary>
    internal string TcpEndpointName = "tcp";

    /// <summary>
    /// The public server URL (domain) configured for this resource.
    /// </summary>
    internal string? PublicServerUrl { get; init; }

    /// <summary>
    /// Optional client certificate used by hosting code (health checks, database
    /// creation, etc.) when connecting to this RavenDB server in secured setups.
    /// </summary>
    internal X509Certificate2? ClientCertificate { get; init; }

    private EndpointReference? _primaryEndpoint;
    private EndpointReference? tcpEndpoint;

    /// <summary>
    /// Gets the primary endpoint for the RavenDB server.
    /// </summary>
    public EndpointReference PrimaryEndpoint => _primaryEndpoint ??= new(this, PrimaryEndpointName);

    /// <summary>
    /// Gets the host endpoint reference for this resource.
    /// </summary>
    public EndpointReferenceExpression Host => PrimaryEndpoint.Property(EndpointProperty.Host);

    /// <summary>
    /// Gets the port endpoint reference for this resource.
    /// </summary>
    public EndpointReferenceExpression Port => PrimaryEndpoint.Property(EndpointProperty.Port);

    /// <summary>
    /// Gets the TCP endpoint for the RavenDB server.
    /// </summary>
    public EndpointReference TcpEndpoint => tcpEndpoint ??= new(this, TcpEndpointName);

    /// <summary>
    /// Gets the connection string expression for the RavenDB server, 
    /// formatted as "http(s)://{Host}:{Port}" depending on the security setting.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression
    {
        get
        {
            if (ExternalUrl is { } externalUrl)
                return ReferenceExpression.Create($"URL={externalUrl}");

            if (IsSecured && !string.IsNullOrEmpty(PublicServerUrl))
                return ReferenceExpression.Create($"URL={PublicServerUrl}");

            return ReferenceExpression.Create(
                $"URL={(IsSecured ? "https://" : "http://")}{PrimaryEndpoint.Property(EndpointProperty.Host)}:{PrimaryEndpoint.Property(EndpointProperty.Port)}");
        }
    }

    /// <summary>
    /// Gets the connection URI expression for the RavenDB server.
    /// </summary>
    /// <remarks>
    /// Format: <c>http://{host}:{port}</c> or <c>https://{host}:{port}</c> depending on security settings.
    /// </remarks>
    public ReferenceExpression UriExpression => ExternalUrl ?? ReferenceExpression.Create($"{(IsSecured ? "https://" : "http://")}{Host}:{Port}");

    /// <summary>
    /// Where consumers connect when the server is not deployed as a container in publish mode: an existing server,
    /// a RavenDB Cloud product, a cluster run by the operator. <see langword="null"/> means the container itself.
    /// </summary>
    /// <remarks>
    /// While it is set, nothing the resource exposes refers to its container endpoints. A compute environment that
    /// no longer contains the container would otherwise fail to resolve them.
    /// </remarks>
    internal ReferenceExpression? ExternalUrl { get; private set; }

    /// <summary>
    /// Takes the server out of the published artifacts and points its consumers at <paramref name="url"/>.
    /// </summary>
    internal void PublishAsExternal(ReferenceExpression url, bool createsDatabases = false)
    {
        ExternalUrl = url;
        CreatesDatabasesWhenExternal = createsDatabases;
    }

    /// <summary>
    /// Whether the strategy that published the server as external creates the <see cref="DatabasesToCreate"/> itself.
    /// </summary>
    internal bool CreatesDatabasesWhenExternal { get; private set; }

    private readonly Dictionary<string, string> _databases = new();
    private readonly HashSet<string> _databasesToCreate = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets a read-only dictionary of databases associated with this server resource.
    /// The key represents the resource name, and the value represents the database name.
    /// </summary>
    public IReadOnlyDictionary<string, string> Databases => _databases;

    /// <summary>
    /// Names of the databases declared with <c>ensureCreated: true</c>. They are created by the AppHost in run
    /// mode and by a bootstrap workload in the published artifacts.
    /// </summary>
    internal IReadOnlyCollection<string> DatabasesToCreate => _databasesToCreate;

    /// <summary>
    /// Whether the license was supplied as a literal string (settings or environment dictionary), which ends up
    /// inlined in published artifacts. Cleared by <c>WithLicense(parameter)</c>.
    /// </summary>
    internal bool HasLiteralLicense { get; set; }

    /// <summary>
    /// Adds a database to the resource.
    /// </summary>
    /// <param name="name">The name of the resource to associate with the database.</param>
    /// <param name="databaseName">The name of the database to add.</param>
    /// <param name="ensureCreated">Whether the database is created when the server starts.</param>
    internal void AddDatabase(string name, string databaseName, bool ensureCreated)
    {
        _databases.TryAdd(name, databaseName);

        if (ensureCreated)
        {
            _databasesToCreate.Add(databaseName);
        }
    }

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        if (ExternalUrl is { } externalUrl)
        {
            // Host and port are not known separately for an external server.
            yield return new("Uri", externalUrl);
            yield break;
        }

        yield return new("Host", ReferenceExpression.Create($"{Host}"));
        yield return new("Port", ReferenceExpression.Create($"{Port}"));
        yield return new("Uri", UriExpression);
    }
}
