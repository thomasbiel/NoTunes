namespace NoTunes.Apple;

internal static class SyncProtocol
{
    public const long Session = 2;
    public const long CapabilityVersion = 1;
    public const long DeltaSync = 1;
    public const long NoSync = 0;
    public const long AssetUnavailable = 16;
    public const long AudioPart = 1;
    public const long ArtworkPart = 2;
    public const long GeniusPart = 4;
    public const long AudioWithGenius = AudioPart | GeniusPart;
    public const int MessageLimit = 10000;
    public const int MaxTrackChanges = 499;
    public const int MaxOperationBytes = 4 * 1024 * 1024;
    public const int MaxCatalogueBytes = 64 * 1024 * 1024;
    public const int MaxPreferenceBytes = 1024 * 1024;
    public const int MusicFolderCount = 50;
    public const ulong MinimumPersistentId = 1000;
    public const long MetadataReserveBytes = 64 * 1024 * 1024;
    public const string HostType = "iTunes";
    public const string HostVersion = "13.2.4.123";
    public const string WindowsVersion = "10.0";
    public const string VoiceMemoAnchor = "1";
    public const string InitialMediaAnchor = "0";
    public const uint UninitializedMediaRevision = 0;
    public const long FirstMediaRevision = 1;
}
