namespace NoTunes.Music;

internal static class Sha256Text
{
    private const int HexLength = 64;

    public static bool IsValid(string? value)
    {
        return value is { Length: HexLength } && value.All(char.IsAsciiHexDigit);
    }
}
