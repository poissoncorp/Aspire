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
}
