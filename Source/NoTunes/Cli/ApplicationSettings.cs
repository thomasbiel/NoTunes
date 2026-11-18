using System.Configuration;

namespace NoTunes.Cli;

internal static class ApplicationSettings
{
    private const string ConfigurationFileName = "NoTunes.dll.config";

    internal static string DefaultFilePath => Path.Combine(AppContext.BaseDirectory, ConfigurationFileName);

    internal static KeyValueConfigurationCollection Read(string? configurationFile)
    {
        configurationFile ??= DefaultFilePath;
        try
        {
            var mapping = new ExeConfigurationFileMap { ExeConfigFilename = configurationFile };
            return ConfigurationManager.OpenMappedExeConfiguration(mapping, ConfigurationUserLevel.None)
                .AppSettings.Settings;
        }
        catch (ConfigurationErrorsException error)
        {
            throw new InvalidDataException(
                $"Could not read application configuration '{configurationFile}': {error.BareMessage}", error);
        }
    }
}
