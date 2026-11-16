using System.Configuration;

namespace NoTunes.Cli;

public static class SourceSettings
{
    private const string ConfigurationFileName = "NoTunes.dll.config";
    private const string SourceFolderSettingName = "MusicSourceFolder";
    public static string ResolveFolder(string? overrideFolder, string? configurationFile = null)
    {
        if (overrideFolder is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(overrideFolder);
            return overrideFolder;
        }
        configurationFile ??= Path.Combine(AppContext.BaseDirectory, ConfigurationFileName);
        try
        {
            var mapping = new ExeConfigurationFileMap { ExeConfigFilename = configurationFile };
            var configuration = ConfigurationManager.OpenMappedExeConfiguration(mapping, ConfigurationUserLevel.None);
            var folder = configuration.AppSettings.Settings[SourceFolderSettingName]?.Value;
            if (string.IsNullOrWhiteSpace(folder))
                throw new InvalidDataException($"{SourceFolderSettingName} is missing or empty in '{configurationFile}'. " +
                    "Set it in app.config or supply a source folder on the command line.");
            return folder;
        }
        catch (ConfigurationErrorsException e)
        {
            throw new InvalidDataException($"Could not read application configuration '{configurationFile}': {e.BareMessage}", e);
        }
    }
}
