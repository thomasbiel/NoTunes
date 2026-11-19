using System.Globalization;

namespace NoTunes.Music;

internal static class ReceiverGroupName
{
    private const CompareOptions MatchingOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    internal static bool Matches(string? left, string? right)
    {
        if (left is null || right is null) return left == right;

        // Linguistic comparison ignores NUL; compare the album/artist key components separately.
        var leftParts = left.Split(CatalogueReader.GroupKeySeparator);
        var rightParts = right.Split(CatalogueReader.GroupKeySeparator);
        if (leftParts.Length != rightParts.Length) return false;

        for (var index = 0; index < leftParts.Length; index++)
        {
            if (CultureInfo.InvariantCulture.CompareInfo.Compare(
                leftParts[index], rightParts[index], MatchingOptions) != 0)
            {
                return false;
            }
        }

        return true;
    }
}
