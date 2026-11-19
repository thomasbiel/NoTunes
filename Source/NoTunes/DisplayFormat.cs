using System.Globalization;

namespace NoTunes;

internal static class DisplayFormat
{
    private const int BytesPerUnit = 1000;
    private const int DecimalPlaces = 1;
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

    internal static string Number(long value)
    {
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }

    internal static string Bytes(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);

        return Bytes((ulong)value);
    }

    internal static string Bytes(ulong value)
    {
        var scaled = (decimal)value;
        var unit = 0;
        while (unit < ByteUnits.Length - 1
            && decimal.Round(scaled, DecimalPlaces, MidpointRounding.AwayFromZero) >= BytesPerUnit)
        {
            scaled /= BytesPerUnit;
            unit++;
        }

        scaled = decimal.Round(scaled, DecimalPlaces, MidpointRounding.AwayFromZero);
        var number = scaled.ToString(unit == 0 ? "N0" : "F1", CultureInfo.InvariantCulture);
        return number + " " + ByteUnits[unit];
    }

    internal static string Bytes(string value)
    {
        return ulong.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var bytes)
            ? Bytes(bytes) : value;
    }
}
