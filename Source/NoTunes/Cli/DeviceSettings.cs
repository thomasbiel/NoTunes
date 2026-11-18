namespace NoTunes.Cli;

internal static class DeviceSettings
{
    private const string DeviceSettingName = "DeviceUdid";
    private const string AddressSettingName = "DeviceAddress";

    internal static void ApplyDefaults(DeviceOptions options, string? configurationFile = null)
    {
        var needsDevice = options.Device is null;
        var needsAddress = options.Wifi && options.Address is null;
        if (!needsDevice && !needsAddress) return;

        var settings = ApplicationSettings.Read(configurationFile);
        if (needsDevice) options.Device = NonEmpty(settings[DeviceSettingName]?.Value);
        if (needsAddress) options.Address = NonEmpty(settings[AddressSettingName]?.Value);
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
