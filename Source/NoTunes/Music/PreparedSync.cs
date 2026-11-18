namespace NoTunes.Music;

internal sealed record PreparedSync(
    SyncPlan Plan,
    IReadOnlyList<PreparedTrack> Additions,
    IReadOnlyList<PreparedTrack> Artwork);
