using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker.Resources;
using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes;

namespace CommunityToolkit.Aspire.Hosting.RavenDB;

/// <summary>Where a client certificate an application brings comes from.</summary>
internal enum RavenDBClientCertificateSource
{
    /// <summary>A .pfx file on the machine that deploys, mounted by Docker Compose.</summary>
    File,

    /// <summary>An existing Kubernetes Secret holding <c>client.pfx</c>.</summary>
    KubernetesSecret,
}

/// <summary>
/// A client certificate the application brings for <paramref name="Server"/>, issued by whoever owns the server. The
/// deployment only delivers it and issues no certificate of its own to the application.
/// </summary>
/// <param name="Server">The server the certificate is for.</param>
/// <param name="Source">Where it comes from.</param>
/// <param name="Location">The file path or the Secret name.</param>
/// <param name="CertificateAuthority">The Secret with the server's certificate authority, for Kubernetes.</param>
internal sealed record RavenDBClientCertificateAnnotation(
    RavenDBServerResource Server,
    RavenDBClientCertificateSource Source,
    string Location,
    string? CertificateAuthority = null) : IResourceAnnotation;

/// <summary>
/// How an application gets its client certificate in Docker Compose: a secret mounted as a file, and the setting the
/// RavenDB client integration reads the file path from. Used for issued and for brought certificates alike.
/// </summary>
internal static class RavenDBComposeCertificates
{
    /// <param name="file">The compose file being written.</param>
    /// <param name="server">The server the certificate is for.</param>
    /// <param name="consumer">The application.</param>
    /// <param name="certificateFile">The .pfx file, as the compose file refers to it.</param>
    public static void Mount(ComposeFile file, RavenDBServerResource server, RavenDBConsumer consumer, string certificateFile)
    {
        var (_, service) = file.Services
            .FirstOrDefault(s => string.Equals(s.Key, consumer.Resource.Name, StringComparison.OrdinalIgnoreCase));

        // The compose file of another environment.
        if (service is null)
        {
            return;
        }

        var secret = $"ravendb-{server.Name}-{consumer.Resource.Name}-certificate".ToLowerInvariant();
        var target = $"ravendb-{server.Name}.pfx".ToLowerInvariant();

        file.Secrets[secret] = new Secret { File = certificateFile };
        service.Secrets.RemoveAll(s => string.Equals(s.Source, secret, StringComparison.Ordinal));
        service.Secrets.Add(new SecretReference { Source = secret, Target = target });

        foreach (var connectionName in consumer.ConnectionNames)
        {
            service.Environment[RavenDBConsumers.CertificatePathVariable(connectionName)] = $"/run/secrets/{target}";
        }
    }
}
