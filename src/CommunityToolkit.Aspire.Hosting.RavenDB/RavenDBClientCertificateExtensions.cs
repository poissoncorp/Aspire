using Aspire.Hosting.ApplicationModel;
using CommunityToolkit.Aspire.Hosting.RavenDB;

#pragma warning disable ASPIREATS001 // AspireExport is experimental

namespace Aspire.Hosting;

/// <summary>
/// Gives applications the RavenDB client certificates that the owner of a server issued for them.
/// </summary>
public static class RavenDBClientCertificateExtensions
{
    /// <summary>
    /// Gives the application a client certificate that the owner of the server issued for it, typically for a server
    /// published with <see cref="RavenDBBuilderExtensions.PublishAsExisting"/>. In Docker Compose the file is mounted
    /// into the container and passed to the RavenDB client integration through
    /// <c>Aspire:RavenDB:Client:&lt;connection name&gt;:CertificatePath</c>. The deployment issues no certificate of its
    /// own to the application.
    /// </summary>
    /// <typeparam name="T">The application resource.</typeparam>
    /// <param name="builder">The resource builder for the application.</param>
    /// <param name="database">
    /// A database of the server the certificate is for; the certificate serves every connection of the application to
    /// that server. The application references the database or the server.
    /// </param>
    /// <param name="path">
    /// The .pfx file, without a password, readable by the user the application's container runs as. A relative path is
    /// relative to the AppHost directory.
    /// </param>
    /// <param name="certificateAuthority">
    /// The certificate authority (PEM) that issued the server's certificate, when it is not publicly trusted. The
    /// application then trusts it as well. A relative path is relative to the AppHost directory.
    /// </param>
    /// <returns>The <see cref="IResourceBuilder{T}"/> for the application.</returns>
    [AspireExport("withRavenDBDatabaseClientCertificateFile", MethodName = "withRavenDBClientCertificateFile")]
    public static IResourceBuilder<T> WithRavenDBClientCertificateFile<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<RavenDBDatabaseResource> database,
        string path,
        string? certificateAuthority = null)
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Relative to the AppHost here; the compose file gets them relative to itself.
        var appHost = builder.ApplicationBuilder.AppHostDirectory;
        var file = Path.GetFullPath(path, appHost);
        var authority = certificateAuthority is null ? null : Path.GetFullPath(certificateAuthority, appHost);

        return builder.WithAnnotation(RavenDBClientCertificates.Single(
            builder.Resource,
            new RavenDBClientCertificateAnnotation(database.Resource.Parent, RavenDBClientCertificateSource.File, file, authority)));
    }
}
