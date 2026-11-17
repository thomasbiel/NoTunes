namespace NoTunes.Music;

internal sealed record InitializationState(
    byte[] Preferences,
    long DatabaseId,
    long ContainerId,
    byte[] Operation,
    CatalogueCheckpoint Before,
    byte[]? Signature = null);
