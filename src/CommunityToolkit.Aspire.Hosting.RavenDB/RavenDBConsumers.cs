using Aspire.Hosting.ApplicationModel;

namespace CommunityToolkit.Aspire.Hosting.RavenDB;

/// <summary>
/// A resource that references a RavenDB server: the deployment strategies give it a client certificate of its own,
/// with access to the databases it references and nothing else, unless it brings one.
/// </summary>
/// <param name="Resource">The application.</param>
/// <param name="ConnectionNames">Names of the referenced resources: the server, or its databases.</param>
/// <param name="Databases">Databases the certificate grants access to.</param>
/// <param name="OwnCertificates">Certificates the application brings for this server.</param>
internal sealed record RavenDBConsumer(
    IResource Resource,
    IReadOnlyList<string> ConnectionNames,
    IReadOnlyList<string> Databases,
    IReadOnlyList<RavenDBClientCertificateAnnotation> OwnCertificates)
{
    /// <summary>Whether the application brings a certificate, so the deployment must not issue one.</summary>
    public bool BringsOwnCertificate => OwnCertificates.Count > 0;

    public RavenDBClientCertificateAnnotation? OwnCertificate(RavenDBClientCertificateSource source) =>
        OwnCertificates.FirstOrDefault(c => c.Source == source);
}

internal static class RavenDBConsumers
{
    // What WithReference(...) records on the referencing resource; Aspire has no public constant for it.
    private const string ReferenceRelationship = "Reference";

    public static IReadOnlyList<RavenDBConsumer> Find(DistributedApplicationModel model, RavenDBServerResource server)
    {
        var consumers = new List<RavenDBConsumer>();

        foreach (var resource in model.Resources)
        {
            if (ReferenceEquals(resource, server) || resource is RavenDBDatabaseResource)
            {
                continue;
            }

            var connectionNames = new SortedSet<string>(StringComparer.Ordinal);
            var databases = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var relationship in resource.Annotations.OfType<ResourceRelationshipAnnotation>())
            {
                if (!string.Equals(relationship.Type, ReferenceRelationship, StringComparison.Ordinal))
                {
                    continue;
                }

                if (ReferenceEquals(relationship.Resource, server))
                {
                    // A reference to the server reaches every database declared on it.
                    connectionNames.Add(server.Name);
                    databases.UnionWith(server.Databases.Values);
                }
                else if (relationship.Resource is RavenDBDatabaseResource database && ReferenceEquals(database.Parent, server))
                {
                    connectionNames.Add(database.Name);
                    databases.Add(database.DatabaseName);
                }
            }

            if (connectionNames.Count > 0)
            {
                consumers.Add(new RavenDBConsumer(resource, [.. connectionNames], [.. databases], OwnCertificatesFor(resource, server)));
            }
        }

        return consumers;
    }

    /// <summary>The setting the RavenDB client integration reads the certificate path from, for one connection.</summary>
    public static string CertificatePathVariable(string connectionName) => $"Aspire__RavenDB__Client__{connectionName}__CertificatePath";

    public static IReadOnlyList<RavenDBClientCertificateAnnotation> OwnCertificatesFor(IResource resource, RavenDBServerResource server) =>
        [.. resource.Annotations.OfType<RavenDBClientCertificateAnnotation>().Where(c => ReferenceEquals(c.Server, server))];
}
