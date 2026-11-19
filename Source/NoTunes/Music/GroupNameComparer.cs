namespace NoTunes.Music;

internal sealed class GroupNameComparer : IEqualityComparer<string>
{
    public static readonly GroupNameComparer Instance = new();

    private GroupNameComparer() { }

    public static string Normalize(string value)
    {
        // Keep stored checkpoint names compatible; equality folds ASCII inside Unicode names too.
        return value.All(char.IsAscii) ? value.ToUpperInvariant() : value;
    }

    public bool Equals(string? left, string? right)
    {
        if (StringComparer.Ordinal.Equals(left, right)) return true;

        if (left is null || right is null || left.Length != right.Length) return false;

        for (var index = 0; index < left.Length; index++)
        {
            if (FoldAscii(left[index]) != FoldAscii(right[index])) return false;
        }

        return true;
    }

    public int GetHashCode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var hash = new HashCode();
        foreach (var character in value)
        {
            hash.Add(FoldAscii(character));
        }

        return hash.ToHashCode();
    }

    private static char FoldAscii(char value)
    {
        return value is >= 'a' and <= 'z' ? (char)(value - ('a' - 'A')) : value;
    }
}
