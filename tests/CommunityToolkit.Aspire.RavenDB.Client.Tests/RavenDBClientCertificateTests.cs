using Microsoft.Extensions.Hosting;

namespace CommunityToolkit.Aspire.RavenDB.Client.Tests;

public class RavenDBClientCertificateTests
{
    [Fact]
    public void CertificateThatCannotBeReadFailsNamingTheFile()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pfx");

        var exception = Assert.Throws<InvalidOperationException>(() => builder.AddRavenDBClient(new RavenDBClientSettings
        {
            Urls = ["https://ravendb.example.test"],
            DatabaseName = "orders",
            CertificatePath = path,
        }));

        Assert.Contains($"The RavenDB client certificate '{path}' cannot be read", exception.Message, StringComparison.Ordinal);
    }
}
