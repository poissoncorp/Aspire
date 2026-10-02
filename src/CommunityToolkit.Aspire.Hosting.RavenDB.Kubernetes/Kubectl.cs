using System.ComponentModel;
using System.Diagnostics;
using Aspire.Hosting.Kubernetes;

namespace CommunityToolkit.Aspire.Hosting.RavenDB.Kubernetes;

/// <summary>Runs kubectl against the cluster the chart is deployed to; replaced in tests.</summary>
internal interface IKubectl
{
    Task<KubectlResult> TryRunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

internal readonly record struct KubectlResult(int ExitCode, string Output, string Error);

internal static class KubectlExtensions
{
    /// <summary>The output; fails when kubectl does.</summary>
    public static async Task<string> RunAsync(this IKubectl kubectl, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await kubectl.TryRunAsync(arguments, cancellationToken).ConfigureAwait(false);

        return result.ExitCode == 0
            ? result.Output
            : throw new InvalidOperationException($"kubectl {string.Join(' ', arguments)} failed: {result.Error.Trim()}");
    }

    /// <summary>The output, empty when kubectl fails: for state that is only reported, never acted on.</summary>
    public static async Task<string> OutputAsync(this IKubectl kubectl, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        (await kubectl.TryRunAsync(arguments, cancellationToken).ConfigureAwait(false)).Output;
}

/// <summary>kubectl, pointed at the namespace and kubeconfig Helm deploys with.</summary>
internal sealed class Kubectl(IReadOnlyList<string> globalArguments) : IKubectl
{
    public static async Task<Kubectl> CreateAsync(KubernetesEnvironmentResource environment, CancellationToken cancellationToken)
    {
        // One stalled call must not outlive the step's own deadlines.
        var arguments = new List<string> { "--request-timeout=30s" };

        if (environment.KubeConfigPath is { Length: > 0 } kubeConfig)
        {
            arguments.AddRange(["--kubeconfig", kubeConfig]);
        }

        // Aspire deploys to the "default" namespace when none is set, whatever the kube context says.
        var ns = environment.Annotations.OfType<KubernetesNamespaceAnnotation>().LastOrDefault() is { } annotation
            ? await annotation.Namespace.GetValueAsync(cancellationToken).ConfigureAwait(false)
            : null;

        arguments.AddRange(["--namespace", ns is { Length: > 0 } ? ns : "default"]);

        return new Kubectl(arguments);
    }

    public async Task<KubectlResult> TryRunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("kubectl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in globalArguments.Concat(arguments))
        {
            start.ArgumentList.Add(argument);
        }

        Process process;

        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("kubectl could not be started.");
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                "kubectl was not found. Deploying a RavenDB cluster needs kubectl on the PATH, next to Helm.", exception);
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            return new KubectlResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
    }
}
