namespace NoTunes.Music;

internal sealed class GroupNameComparer : IEqualityComparer<string>
{
    public static readonly GroupNameComparer Instance = new();

    private GroupNameComparer() { }

    public static string Normalize(string value)
    {
        return value.All(char.IsAscii) ? value.ToUpperInvariant() : value;
    }

    public bool Equals(string? left, string? right)
    {
        return StringComparer.Ordinal.Equals(left, right)
            || left is not null && right is not null && left.All(char.IsAscii) && right.All(char.IsAscii)
                && StringComparer.OrdinalIgnoreCase.Equals(left, right);
    }

    public int GetHashCode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return (value.All(char.IsAscii) ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).GetHashCode(value);
    }
}
