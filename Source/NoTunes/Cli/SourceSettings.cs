namespace NoTunes.Cli;

public static class SourceSettings
{
    private const string SourceFolderSettingName = "MusicSourceFolder";

    public static string ResolveFolder(string? overrideFolder, string? configurationFile = null)
    {
        if (overrideFolder is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(overrideFolder);
            return overrideFolder;
        }

        configurationFile ??= ApplicationSettings.DefaultFilePath;
        var folder = ApplicationSettings.Read(configurationFile)[SourceFolderSettingName]?.Value;
        if (string.IsNullOrWhiteSpace(folder))
        {
            throw new InvalidDataException($"{SourceFolderSettingName} is missing or empty in '{configurationFile}'. " + "Set it in app.config or supply a source folder on the command line.");
        }

        return folder;
    }
}
