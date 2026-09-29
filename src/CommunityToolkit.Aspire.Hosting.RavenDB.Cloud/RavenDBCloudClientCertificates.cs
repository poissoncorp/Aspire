using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker.Resources;
using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Cloud;

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

    public static string FileName(RavenDBServerResource server, IResource consumer) =>
        $"{server.Name}-{consumer.Name}.pfx".ToLowerInvariant();

    /// <summary>Name of the file in the container; Docker Compose mounts secrets under <c>/run/secrets</c>.</summary>
    public static string SecretTarget(RavenDBServerResource server) => $"ravendb-{server.Name}.pfx".ToLowerInvariant();

    public static string ContainerPath(RavenDBServerResource server) => $"/run/secrets/{SecretTarget(server)}";

    /// <summary>
    /// Mounts each consumer's certificate into its service. The file itself is written by the deploy step; the
    /// compose file only refers to it.
    /// </summary>
    public static void AddToComposeFile(ComposeFile file, RavenDBServerResource server, IReadOnlyList<RavenDBConsumer> consumers)
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
                service.Environment[RavenDBConsumers.CertificatePathVariable(connectionName)] = ContainerPath(server);
            }
        }
    }
}
