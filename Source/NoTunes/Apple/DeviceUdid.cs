namespace NoTunes.Apple;

internal static class DeviceUdid
{
    public const int ModernCompactLength = 24;
    public const int ModernPrefixLength = 8;
    public const string Separator = "-";

    public static string Normalize(string udid) => udid.Replace(Separator, string.Empty, StringComparison.Ordinal);
}
