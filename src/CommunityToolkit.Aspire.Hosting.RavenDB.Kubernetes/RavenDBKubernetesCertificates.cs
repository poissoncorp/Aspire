using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Kubernetes.Resources;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Kubernetes;

/// <summary>
/// How an application gets its client certificate in Kubernetes: a Secret mounted as a file, the setting the RavenDB
/// client integration reads the file path from, and for a server with a private certificate authority, that
/// authority next to the image's own trusted roots. Used for issued and for brought certificates alike.
/// </summary>
internal static class RavenDBKubernetesCertificates
{
    /// <summary>Kubernetes object names are lowercase.</summary>
    public static string NameOf(IResource resource) => resource.Name.ToLowerInvariant();

    /// <param name="k8s">The application's Kubernetes objects.</param>
    /// <param name="server">The server the certificate is for.</param>
    /// <param name="consumer">The application.</param>
    /// <param name="certificateSecret">The Secret holding <c>client.pfx</c>.</param>
    /// <param name="certificateAuthoritySecret">The Secret holding <c>ca.crt</c>, when the server's authority is private.</param>
    public static void Mount(
        KubernetesResource k8s,
        RavenDBServerResource server,
        RavenDBConsumer consumer,
        string certificateSecret,
        string? certificateAuthoritySecret)
    {
        if (k8s.Workload?.PodTemplate?.Spec is not { } pod)
        {
            return;
        }

        var name = NameOf(server);
        var certificateVolume = $"{name}-client-certificate";
        var authorityVolume = $"{name}-certificate-authority";
        var authorityDirectory = $"/ravendb/{name}-ca";

        pod.Volumes.Add(new VolumeV1 { Name = certificateVolume, Secret = new SecretVolumeSourceV1 { SecretName = certificateSecret } });

        if (certificateAuthoritySecret is not null)
        {
            pod.Volumes.Add(new VolumeV1 { Name = authorityVolume, Secret = new SecretVolumeSourceV1 { SecretName = certificateAuthoritySecret } });
        }

        foreach (var container in pod.Containers)
        {
            container.VolumeMounts.Add(new VolumeMountV1 { Name = certificateVolume, MountPath = CertificateDirectory(server), ReadOnly = true });

            foreach (var connectionName in consumer.ConnectionNames)
            {
                container.Env.Add(new EnvVarV1 { Name = RavenDBConsumers.CertificatePathVariable(connectionName), Value = CertificatePath(server) });
            }

            if (certificateAuthoritySecret is not null)
            {
                container.VolumeMounts.Add(new VolumeMountV1 { Name = authorityVolume, MountPath = authorityDirectory, ReadOnly = true });

                // .NET on Linux reads its trusted roots from the directories in SSL_CERT_DIR: keep the image's own
                // and add the server's certificate authority.
                container.Env.RemoveAll(e => e.Name == "SSL_CERT_DIR");
                container.Env.Add(new EnvVarV1 { Name = "SSL_CERT_DIR", Value = $"/etc/ssl/certs:{authorityDirectory}" });
            }
        }
    }

    private static string CertificateDirectory(RavenDBServerResource server) => $"/ravendb/{NameOf(server)}";

    private static string CertificatePath(RavenDBServerResource server) => $"{CertificateDirectory(server)}/client.pfx";
}
