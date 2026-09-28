using System.Security.Cryptography.X509Certificates;
using Raven.Client.Documents;
using Raven.Client.Exceptions;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Raven.Client.ServerWide.Operations.Certificates;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Cloud;

/// <summary>
/// The server-side operations a deployment performs on a RavenDB Cloud product, authenticated with the product's
/// admin certificate. Behind an interface so the deployment logic can be tested without a server.
/// </summary>
internal interface IRavenDBServerAdministration : IDisposable
{
    Task<bool> DatabaseExistsAsync(string database, CancellationToken cancellationToken);

    /// <returns><see langword="false"/> when the database was created concurrently.</returns>
    Task<bool> CreateDatabaseAsync(string database, int replicationFactor, CancellationToken cancellationToken);

    /// <returns>The databases the certificate may access, or <see langword="null"/> when the server does not know it.</returns>
    Task<IReadOnlyDictionary<string, DatabaseAccess>?> GetCertificatePermissionsAsync(string thumbprint, CancellationToken cancellationToken);

    /// <summary>Creates a client certificate with <see cref="SecurityClearance.ValidUser"/> clearance.</summary>
    /// <returns>The bundle the server returns (a zip with the .pfx).</returns>
    Task<byte[]> CreateClientCertificateAsync(string name, IReadOnlyDictionary<string, DatabaseAccess> permissions, CancellationToken cancellationToken);

    Task SetCertificatePermissionsAsync(string thumbprint, string name, IReadOnlyDictionary<string, DatabaseAccess> permissions, CancellationToken cancellationToken);

    Task DeleteCertificateAsync(string thumbprint, CancellationToken cancellationToken);
}

internal interface IRavenDBServerAdministrationFactory
{
    /// <summary>Connects to <paramref name="url"/>. The returned instance owns <paramref name="certificate"/>.</summary>
    IRavenDBServerAdministration Create(string url, X509Certificate2? certificate);
}

internal sealed class RavenDBServerAdministrationFactory : IRavenDBServerAdministrationFactory
{
    public IRavenDBServerAdministration Create(string url, X509Certificate2? certificate) =>
        new RavenDBServerAdministration(url, certificate);
}

/// <remarks>
/// Uses the RavenDB client, so the server certificate is validated the way every RavenDB client validates it:
/// <c>RequestExecutor.RemoteCertificateValidationCallback</c> applies.
/// </remarks>
internal sealed class RavenDBServerAdministration : IRavenDBServerAdministration
{
    private readonly X509Certificate2? _certificate;
    private readonly DocumentStore _store;

    public RavenDBServerAdministration(string url, X509Certificate2? certificate)
    {
        _certificate = certificate;
        _store = new DocumentStore { Urls = [url], Certificate = certificate };
        _store.Initialize();
    }

    private ServerOperationExecutor Server => _store.Maintenance.Server;

    public async Task<bool> DatabaseExistsAsync(string database, CancellationToken cancellationToken) =>
        await Server.SendAsync(new GetDatabaseRecordOperation(database), cancellationToken).ConfigureAwait(false) is not null;

    public async Task<bool> CreateDatabaseAsync(string database, int replicationFactor, CancellationToken cancellationToken)
    {
        try
        {
            await Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(database), replicationFactor), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ConcurrencyException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyDictionary<string, DatabaseAccess>?> GetCertificatePermissionsAsync(string thumbprint, CancellationToken cancellationToken)
    {
        var definition = await Server.SendAsync(new GetCertificateOperation(thumbprint), cancellationToken).ConfigureAwait(false);

        if (definition is null)
        {
            return null;
        }

        // Database names are case-insensitive in RavenDB.
        return new Dictionary<string, DatabaseAccess>(definition.Permissions ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public async Task<byte[]> CreateClientCertificateAsync(string name, IReadOnlyDictionary<string, DatabaseAccess> permissions, CancellationToken cancellationToken)
    {
        var certificate = await Server.SendAsync(
            new CreateClientCertificateOperation(name, new Dictionary<string, DatabaseAccess>(permissions), SecurityClearance.ValidUser, password: null),
            cancellationToken).ConfigureAwait(false);

        return certificate.RawData;
    }

    public Task SetCertificatePermissionsAsync(string thumbprint, string name, IReadOnlyDictionary<string, DatabaseAccess> permissions, CancellationToken cancellationToken) =>
        Server.SendAsync(
            new EditClientCertificateOperation(new EditClientCertificateOperation.Parameters
            {
                Thumbprint = thumbprint,
                Name = name,
                Permissions = new Dictionary<string, DatabaseAccess>(permissions),
                Clearance = SecurityClearance.ValidUser,
            }),
            cancellationToken);

    public Task DeleteCertificateAsync(string thumbprint, CancellationToken cancellationToken) =>
        Server.SendAsync(new DeleteCertificateOperation(thumbprint), cancellationToken);

    public void Dispose()
    {
        _store.Dispose();
        _certificate?.Dispose();
    }
}
