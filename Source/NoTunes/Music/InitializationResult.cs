namespace NoTunes.Music;

public sealed record InitializationResult(string Device, bool DryRun, bool AlreadyInitialized, long DatabaseId);
