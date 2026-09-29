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
}
