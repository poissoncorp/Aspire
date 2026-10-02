using Aspire.Components.Common.Tests;
using CommunityToolkit.Aspire.Testing;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Cloud.Tests;

[RequiresDocker]
public class TypeScriptAppHostTests
{
    [Fact]
    public async Task TypeScriptAppHostCompilesAndStarts()
    {
        await TypeScriptAppHostTest.Run(
            appHostProject: "CommunityToolkit.Aspire.Hosting.RavenDB.Cloud.AppHost.TypeScript",
            packageName: "CommunityToolkit.Aspire.Hosting.RavenDB.Cloud",
            exampleName: "ravendb",
            waitForResources: ["ravendb"],
            cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The product's options are only applied by aspire publish: the example sets Azure, which has no default region,
    /// so a Region lost on its way through ATS would fail it.
    /// </summary>
    [Fact]
    public async Task TypeScriptAppHostPublishesWithTheOptionsItConfigures()
    {
        var output = Directory.CreateTempSubdirectory(".ravendb-ts-publish");

        try
        {
            var appHost = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "..", "examples", "ravendb", "CommunityToolkit.Aspire.Hosting.RavenDB.Cloud.AppHost.TypeScript"));
            await ProcessTestUtilities.RunProcessAsync(
                "aspire", ["publish", "--apphost", Path.Combine(appHost, "apphost.mts"), "-o", output.FullName, "--non-interactive", "--nologo"], appHost, TestContext.Current.CancellationToken);

            // The product replaces the server: no container for it.
            var compose = File.ReadAllText(Path.Combine(output.FullName, "docker-compose.yaml"));
            Assert.DoesNotContain("ravendb/ravendb", compose, StringComparison.Ordinal);
        }
        finally
        {
            output.Delete(recursive: true);
        }
    }
}
