namespace CommunityToolkit.Aspire.Utils;

/// <summary>
/// A package that builds on internals of a sibling package released with it works with that very version only. NuGet
/// cannot pin a project reference to an exact version, so a mismatch is caught here, before any of it runs, instead
/// of surfacing as a missing method later.
/// </summary>
internal static class MatchingPackageVersion
{
    /// <param name="sibling">A type of the sibling package.</param>
    /// <param name="self">A type of the calling package.</param>
    public static void Ensure(Type sibling, Type self)
    {
        var expected = self.Assembly.GetName();
        var actual = sibling.Assembly.GetName();

        if (expected.Version != actual.Version)
        {
            throw new InvalidOperationException(
                $"{expected.Name} {expected.Version?.ToString(3)} works with {actual.Name} {expected.Version?.ToString(3)} only, " +
                $"but {actual.Version?.ToString(3)} is installed. Install the same version of both packages.");
        }
    }
}
