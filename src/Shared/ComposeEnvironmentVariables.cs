namespace CommunityToolkit.Aspire.Utils;

/// <summary>
/// How Aspire's Docker Compose publisher names the <c>.env</c> variable of a value only known at deploy time, so a
/// deploy step can fill it in after <c>prepare-&lt;env&gt;</c> wrote it blank.
/// </summary>
internal static class ComposeEnvironmentVariables
{
    /// <summary>The variable of a value expression: <c>{ravendb.url}</c> is <c>RAVENDB_URL</c>.</summary>
    /// <param name="valueExpression">The value expression, such as <c>{ravendb.url}</c>.</param>
    /// <returns>The variable name.</returns>
    public static string NameOf(string valueExpression) =>
        valueExpression
            .Replace("{", string.Empty, StringComparison.Ordinal)
            .Replace("}", string.Empty, StringComparison.Ordinal)
            .Replace('.', '_')
            .Replace('-', '_')
            .ToUpperInvariant();
}
