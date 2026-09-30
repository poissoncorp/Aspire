using CommunityToolkit.Aspire.Testing;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Tests;

/// <summary>
/// The script of the Docker Compose bootstrap service, run as the service runs it: <c>/bin/sh -c</c> in the RavenDB
/// image.
/// </summary>
[RequiresDocker]
public sealed class ComposeBootstrapScriptTests : IDisposable
{
    private const string Server = "http://ravendb:8080";

    private readonly ShellScriptHarness _harness = new ShellScriptHarness()
        .Respond("GET", Server + RavenDBPublishing.ReadinessPath, 200);

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task CreatesOnlyTheMissingDatabases()
    {
        _harness
            .Respond("GET", $"{Server}/databases?name=orders", 200)
            .Respond("PUT", $"{Server}/admin/databases?name=reports*", 201);

        var result = await Run("orders", "reports");

        Assert.True(result.ExitCode == 0, result.ToString());
        var created = Assert.Single(_harness.Requests, r => r.Method == "PUT");
        Assert.Equal($"{Server}/admin/databases?name=reports&replicationFactor=1", created.Url);
        Assert.Equal("""{"DatabaseName":"reports"}""", created.Body);
        Assert.Contains("database orders already exists", result.Output);
        Assert.Contains("created database reports", result.Output);
    }

    [Fact]
    public async Task WaitsUntilTheServerAnswers()
    {
        using var harness = new ShellScriptHarness()
            .Respond("GET", Server + RavenDBPublishing.ReadinessPath, [503, 200]);

        var result = await harness.RunAsync(new Dictionary<string, string>(), "/bin/sh", "-c", RavenDBPublishing.BuildBootstrapScript(Server, []));

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal(2, harness.Requests.Count);
    }

    [Fact]
    public async Task DatabaseCreatedMeanwhileBySomeoneElseIsFine()
    {
        _harness
            .Respond("GET", $"{Server}/databases?name=orders", [404, 200])
            .Respond("PUT", $"{Server}/admin/databases?name=orders*", 409);

        var result = await Run("orders");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Contains("database orders already exists", result.Output);
    }

    [Fact]
    public async Task DatabaseThatCannotBeCreatedFailsTheService()
    {
        _harness.Respond("PUT", $"{Server}/admin/databases?name=orders*", 500);

        var result = await Run("orders", "reports");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("creating database orders failed", result.Error);
        Assert.DoesNotContain(_harness.Requests, r => r.Url.Contains("reports", StringComparison.Ordinal));
    }

    private Task<ShellScriptResult> Run(params string[] databases) =>
        _harness.RunAsync(new Dictionary<string, string>(), "/bin/sh", "-c", RavenDBPublishing.BuildBootstrapScript(Server, databases));
}
