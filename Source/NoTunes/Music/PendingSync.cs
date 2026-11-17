using NoTunes.Apple;

namespace NoTunes.Music;

internal sealed record PendingSync(
    int Version,
    LibraryIdentity Identity,
    CatalogueCheckpoint Before,
    JournalTrack[] Additions,
    DeviceTrack[] Deletions,
    byte[] Operation,
    byte[] Signature);
