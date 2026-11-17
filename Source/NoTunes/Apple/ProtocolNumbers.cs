using System.Globalization;

namespace NoTunes.Apple;

internal static class ProtocolNumbers
{
    public static bool TryParseNonNegativeInt64(string? text, out long value)
    {
        return long.TryParse(
            text,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out value);
    }
}
