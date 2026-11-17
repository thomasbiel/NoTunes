namespace NoTunes.Apple;

public sealed record DeviceInfo(string Udid, string Name, string Model, string IosVersion, string BuildVersion)
{
    internal const string IphoneModelPrefix = "iPhone";
}
