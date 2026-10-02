using Aspire.Components.Common.Tests;
using CommunityToolkit.Aspire.Testing;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Kubernetes.Tests;

[RequiresDocker]
public class TypeScriptAppHostTests
{
    [Fact]
    public async Task TypeScriptAppHostCompilesAndStarts()
    {
        await TypeScriptAppHostTest.Run(
            appHostProject: "CommunityToolkit.Aspire.Hosting.RavenDB.Kubernetes.AppHost.TypeScript",
            packageName: "CommunityToolkit.Aspire.Hosting.RavenDB.Kubernetes",
            exampleName: "ravendb",
            waitForResources: ["ravendb"],
            cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>The cluster's options are only applied by aspire publish, so it is what runs them through ATS.</summary>
    [Fact]
    public async Task TypeScriptAppHostPublishesTheClusterItConfigures()
    {
        var output = Directory.CreateTempSubdirectory(".ravendb-ts-publish");

        try
        {
            var appHost = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "..", "examples", "ravendb", "CommunityToolkit.Aspire.Hosting.RavenDB.Kubernetes.AppHost.TypeScript"));
            await ProcessTestUtilities.RunProcessAsync(
                "aspire", ["publish", "--apphost", Path.Combine(appHost, "apphost.mts"), "-o", output.FullName, "--non-interactive", "--nologo"], appHost, TestContext.Current.CancellationToken);

            var cluster = File.ReadAllText(Path.Combine(output.FullName, "templates", "ravendb-bootstrap", "ravendb.yaml"));
            Assert.Contains("domain: \"ravendb.example.com\"", cluster, StringComparison.Ordinal);
            Assert.Contains("image: \"ravendb/ravendb:7.2.6-ubuntu.24.04-x64\"", cluster, StringComparison.Ordinal);
            Assert.Contains("licenseSecretRef: \"ravendb-license\"", cluster, StringComparison.Ordinal);
            Assert.Contains("tag: \"c\"", cluster, StringComparison.Ordinal);
        }
        finally
        {
            output.Delete(recursive: true);
        }
    }
}
