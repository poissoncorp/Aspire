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
    /// <param name="path">The .pfx file, without a password. A relative path is relative to the AppHost directory.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/> for the application.</returns>
    [AspireExport("withRavenDBDatabaseClientCertificateFile", MethodName = "withRavenDBClientCertificateFile")]
    public static IResourceBuilder<T> WithRavenDBClientCertificateFile<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<RavenDBDatabaseResource> database,
        string path)
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Docker Compose resolves a relative path against the compose file, which lives in the output directory.
        var file = Path.GetFullPath(path, builder.ApplicationBuilder.AppHostDirectory);

        return builder.WithAnnotation(new RavenDBClientCertificateAnnotation(database.Resource.Parent, RavenDBClientCertificateSource.File, file));
    }
}
