namespace NoTunes.Apple;

internal static class SyncPaths
{
    public const string Preferences = "iTunes_Control/iTunes/iTunesPrefs";
    public const string PreferencesPlist = Preferences + ".plist";
    public const string SavedCatalogue = "iTunes_Control/iTunes/iTunesCDB";
    public const string Lock = "iTunes_Control/iTunes/SyncLock";
    public const string MetadataDirectory = "iTunes_Control/Sync/Media";
    public const string Metadata = MetadataDirectory + "/Sync_00000000.plist";
    public const string SignatureSuffix = ".cig";
    public const string Airlock = "Airlock/Media/";
    public const string MusicBucketPrefix = "iTunes_Control/Music/F";
    public const string Mp3Extension = ".mp3";
    public const string DatabaseFile = "MediaLibrary.sqlitedb";
    public const string WalSuffix = "-wal";
}
