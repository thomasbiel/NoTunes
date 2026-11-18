namespace NoTunes.Platform;

public static class SourceDirectoryEntries
{
    public static IEnumerable<SourceEntry> Enumerate(DirectoryInfo directory)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = false,
            AttributesToSkip = 0
        };

        return directory.EnumerateFileSystemInfos(SourceFileDefaults.AllEntriesPattern, options)
            .Select(entry => new SourceEntry(entry.Name, entry.Attributes));
    }
}
