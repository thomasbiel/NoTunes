using NoTunes.Music;
using NoTunes.Platform;

namespace NoTunes.Cli;

public sealed record CommandContext(
    SourceScanner Scanner,
    IUsbMuxTransport Transport,
    SyncEngine SyncEngine,
    LibraryInitializer Initializer,
    TextWriter Output);
