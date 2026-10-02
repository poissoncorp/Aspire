using System.Diagnostics;
using System.Text.Json;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Tests;

/// <summary>
/// Runs a deployment script in the RavenDB image, where it runs when deployed, with a stand-in for curl that records
/// every request and answers from the routes a test declares. Also used by the Kubernetes tests.
/// </summary>
internal sealed class ShellScriptHarness : IDisposable
{
    public const string Image = $"{RavenDBContainerImageTags.Image}:{RavenDBContainerImageTags.Tag}";

    // Records each request in /harness/requests.log and answers from /harness/routes, whose lines are
    // "METHOD<tab>URL pattern<tab>statuses<tab>body file". The first match wins, anything else is a 404. With several
    // statuses ("404,200") the n-th identical request gets the n-th one, and the last one repeats.
    private const string StubCurl = """
        #!/bin/bash
        method="" url="" out="" format="" data="" fail=0
        while (($#)); do
            case "$1" in
                -X) method=$2; shift ;;
                -o) out=$2; shift ;;
                -w) format=$2; shift ;;
                -d) data=$2; shift ;;
                -H|--cert|--key|--cacert|--connect-timeout|--max-time) shift ;;
                --*) ;;
                -*) [[ "$1" == *f* ]] && fail=1 ;;
                *) url=$1 ;;
            esac
            shift
        done

        [[ "$data" != @* ]] || data=$(cat "${data#@}")
        [[ -n "$method" ]] || { [[ -n "$data" ]] && method=POST || method=GET; }

        jq -cn --arg method "$method" --arg url "$url" --arg body "$data" '{$method, $url, $body}' >> /harness/requests.log
        calls=$(jq -c --arg method "$method" --arg url "$url" 'select(.method == $method and .url == $url)' /harness/requests.log | wc -l)

        status=404 body=/dev/null
        while IFS=$'\t' read -r route_method pattern statuses route_body; do
            if [[ "$route_method" == "$method" && "$url" == $pattern ]]; then
                IFS=, read -r -a statuses <<< "$statuses"
                index=$((calls <= ${#statuses[@]} ? calls - 1 : ${#statuses[@]} - 1))
                status=${statuses[$index]} body=/harness/bodies/$route_body
                break
            fi
        done < /harness/routes

        if ((fail && status >= 400)); then
            exit 22
        fi

        cat "$body" > "${out:-/dev/stdout}"
        [[ -z "$format" ]] || printf '%s' "${format//'%{http_code}'/$status}"
        """;

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("ravendb-script-");
    private readonly List<string> _routes = [];
    private readonly List<(string Host, string Container)> _files = [];

    public ShellScriptHarness()
    {
        Directory.CreateDirectory(Path.Combine(_directory.FullName, "bin"));
        Directory.CreateDirectory(Path.Combine(_directory.FullName, "bodies"));
        Directory.CreateDirectory(Path.Combine(_directory.FullName, "files"));
        WriteUnixText(Path.Combine(_directory.FullName, "bin", "curl"), StubCurl);
        WriteUnixText(RequestLog, string.Empty);

        // The image runs as a user of its own (999): it has to enter the directory, run the stub and write the log.
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode all = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            const UnixFileMode enter = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

            File.SetUnixFileMode(_directory.FullName, all | enter | UnixFileMode.UserWrite);
            File.SetUnixFileMode(Path.Combine(_directory.FullName, "bin", "curl"), all | enter | UnixFileMode.UserWrite);
            File.SetUnixFileMode(RequestLog, all | UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite);
        }
    }

    private string RequestLog => Path.Combine(_directory.FullName, "requests.log");

    /// <summary>The requests the script sent, in order.</summary>
    public IReadOnlyList<StubRequest> Requests =>
        [.. File.ReadAllLines(RequestLog).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<StubRequest>(l, JsonSerializerOptions.Web)!)];

    /// <param name="method">The HTTP method.</param>
    /// <param name="url">The URL, a bash pattern: <c>*</c> matches anything.</param>
    /// <param name="status">The status code.</param>
    /// <param name="body">The response body.</param>
    public ShellScriptHarness Respond(string method, string url, int status, string body = "") => Respond(method, url, [status], body);

    /// <param name="method">The HTTP method.</param>
    /// <param name="url">The URL, a bash pattern: <c>*</c> matches anything.</param>
    /// <param name="statuses">The status code of each identical request in turn; the last one repeats.</param>
    /// <param name="body">The response body.</param>
    public ShellScriptHarness Respond(string method, string url, int[] statuses, string body = "")
    {
        var name = _routes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(_directory.FullName, "bodies", name), body);
        _routes.Add($"{method}\t{url}\t{string.Join(',', statuses)}\t{name}");
        return this;
    }

    /// <summary>Mounts a file into the container, read-only.</summary>
    public ShellScriptHarness Mount(string containerPath, byte[] content)
    {
        var host = Path.Combine(_directory.FullName, "files", _files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.WriteAllBytes(host, content);
        _files.Add((host, containerPath));
        return this;
    }

    public ShellScriptHarness Mount(string containerPath, string content) => Mount(containerPath, System.Text.Encoding.UTF8.GetBytes(content.ReplaceLineEndings("\n")));

    public async Task<ShellScriptResult> RunAsync(IReadOnlyDictionary<string, string> environment, string entrypoint, params string[] arguments)
    {
        WriteUnixText(Path.Combine(_directory.FullName, "routes"), string.Join('\n', _routes) + "\n");

        var name = $"ravendb-script-{Guid.NewGuid():N}";
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        string[] options =
        [
            "run", "--rm", "--name", name, "--network", "none",
            "-v", $"{_directory.FullName}:/harness",
            "-e", "PATH=/harness/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
            .. _files.SelectMany(f => new[] { "-v", $"{f.Host}:{f.Container}:ro" }),
            .. environment.SelectMany(e => new[] { "-e", $"{e.Key}={e.Value}" }),
            "--entrypoint", entrypoint,
            Image,
            .. arguments,
        ];

        foreach (var option in options)
        {
            start.ArgumentList.Add(option);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            using var remove = Process.Start("docker", ["rm", "--force", name]);
            await remove.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        return new ShellScriptResult(process.ExitCode, await output, await error);
    }

    public void Dispose() => _directory.Delete(recursive: true);

    // Bash reads the stub, the routes and the log: no carriage returns.
    private static void WriteUnixText(string path, string content) => File.WriteAllText(path, content.ReplaceLineEndings("\n"));
}

internal sealed record StubRequest(string Method, string Url, string Body);

internal sealed record ShellScriptResult(int ExitCode, string Output, string Error)
{
    public override string ToString() => $"exit {ExitCode}{Environment.NewLine}{Output}{Error}";
}
