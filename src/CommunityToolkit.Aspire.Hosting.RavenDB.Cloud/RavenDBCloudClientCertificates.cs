using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker.Resources;
using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Cloud;

/// <summary>
/// A resource that references a server published to RavenDB Cloud: it gets a client certificate of its own, with
/// access to the databases it references and nothing else.
/// </summary>
/// <param name="Resource">The application.</param>
/// <param name="ConnectionNames">Names of the referenced resources: the server, or its databases.</param>
/// <param name="Databases">Databases the certificate grants access to.</param>
internal sealed record RavenDBCloudConsumer(IResource Resource, IReadOnlyList<string> ConnectionNames, IReadOnlyList<string> Databases);

/// <summary>A client certificate the deployment issues and the file it writes it to.</summary>
internal sealed record ClientCertificateRequest(string Consumer, string CertificateName, IReadOnlyList<string> Databases, string FilePath);

/// <summary>
/// How client certificates reach the applications: a Docker Compose secret per application, mounted as a file,
/// and the setting the RavenDB client integration reads the file path from.
/// </summary>
internal static class RavenDBCloudClientCertificates
{
    /// <summary>Directory next to the compose file that holds the issued certificates.</summary>
    public const string DirectoryName = "ravendb-certs";

    // What WithReference(...) records on the referencing resource; Aspire has no public constant for it.
    private const string ReferenceRelationship = "Reference";

    public static IReadOnlyList<RavenDBCloudConsumer> FindConsumers(DistributedApplicationModel model, RavenDBServerResource server)
    {
        var consumers = new List<RavenDBCloudConsumer>();

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
                consumers.Add(new RavenDBCloudConsumer(resource, [.. connectionNames], [.. databases]));
            }
        }

        return consumers;
    }

    public static string FileName(RavenDBServerResource server, IResource consumer) =>
        $"{server.Name}-{consumer.Name}.pfx".ToLowerInvariant();

    /// <summary>Name of the file in the container; Docker Compose mounts secrets under <c>/run/secrets</c>.</summary>
    public static string SecretTarget(RavenDBServerResource server) => $"ravendb-{server.Name}.pfx".ToLowerInvariant();

    public static string ContainerPath(RavenDBServerResource server) => $"/run/secrets/{SecretTarget(server)}";

    /// <summary>The setting the RavenDB client integration reads for the connection named <paramref name="connectionName"/>.</summary>
    public static string CertificatePathVariable(string connectionName) => $"Aspire__RavenDB__Client__{connectionName}__CertificatePath";

    /// <summary>
    /// Mounts each consumer's certificate into its service. The file itself is written by the deploy step; the
    /// compose file only refers to it.
    /// </summary>
    public static void AddToComposeFile(ComposeFile file, RavenDBServerResource server, IReadOnlyList<RavenDBCloudConsumer> consumers)
    {
        foreach (var consumer in consumers)
        {
            var (_, service) = file.Services
                .FirstOrDefault(s => string.Equals(s.Key, consumer.Resource.Name, StringComparison.OrdinalIgnoreCase));

            if (service is null)
            {
                continue;
            }

            var secretName = $"ravendb-{server.Name}-{consumer.Resource.Name}-certificate".ToLowerInvariant();

            file.Secrets[secretName] = new Secret { File = $"./{DirectoryName}/{FileName(server, consumer.Resource)}" };

            service.Secrets.RemoveAll(s => string.Equals(s.Source, secretName, StringComparison.Ordinal));
            service.Secrets.Add(new SecretReference { Source = secretName, Target = SecretTarget(server) });

            foreach (var connectionName in consumer.ConnectionNames)
            {
                service.Environment[CertificatePathVariable(connectionName)] = ContainerPath(server);
            }
        }
    }
}
