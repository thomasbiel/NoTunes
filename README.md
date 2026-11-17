# NoTunes

A .NET 10 command-line application with Windows and Linux platform providers.
The intended behavior is to mirror
all MP3 files in a source folder and its subfolders to the iPhone's built-in Music
app over USB. No GUI, playlists, transcoding, WSL or external sync executable.

`sync` mirrors MP3 files to an existing, initialized iPhone Music library over
USB. It preserves byte-identical tracks, transfers additions through AFC and
removes obsolete computer-synced music through signed ATC delta sessions.
`sync --dry-run` reads the source and device and prints the actual change plan
without starting a write session.

## Build and test

```powershell
dotnet build NoTunes.slnx
dotnet test Tests/NoTunes.Tests.csproj
```

CLI parsing uses `CommandLineParser` 2.9.1. Tests use NUnit 5.0.0,
`NUnit3TestAdapter` 6.3.1 and `Microsoft.NET.Test.Sdk` 18.12.0 with `dotnet test`
and Visual Studio Test Explorer. NUnit's `ThrowsAsync`, `CatchAsync` and
`DoesNotThrowAsync` assertions must be awaited in `async Task` tests.
All application output and help text are in English.
TLS uses the managed `BouncyCastle.Cryptography` library. Pairing private keys
stay in memory; no Windows key containers or trusted certificates are installed.

The Microsoft runtime packages `Microsoft.Extensions.DependencyInjection`,
`Microsoft.Extensions.DependencyInjection.Abstractions` and
`System.Configuration.ConfigurationManager` use version 10.0.15, as do the
transitive `System.Diagnostics.EventLog` and
`System.Security.Cryptography.ProtectedData` packages. These are the latest
stable 10.x versions checked on 16 November 2026; the NUnit framework, adapter
and Microsoft test SDK versions above are the latest stable releases checked
on that date. Runtime packages remain on the .NET 10 release line.

MP3 metadata is read with **TagLibSharp 2.3.0**, licensed under **LGPL-2.1-only**.
The repository includes its [copyright notice](Licenses/TagLibSharp-NOTICE.txt),
the [full LGPL 2.1 license](Licenses/LGPL-2.1.txt), and
[third-party notices](THIRD-PARTY-NOTICES.md). These files also accompany build
and publish output. TagLibSharp is used unmodified as a separate assembly.

## Source configuration

The application project is `Source/NoTunes/NoTunes.csproj`; the NUnit project is
`Tests/NoTunes.Tests.csproj`. The combined application, platform projects and tests
are included in `NoTunes.slnx`.

The default source is set in `Source/NoTunes/app.config`:

```xml
<configuration>
  <appSettings>
    <add key="MusicSourceFolder" value="\\192.168.0.150\music" />
  </appSettings>
</configuration>
```

The build copies this file to `NoTunes.dll.config` beside the executable. `scan`
and `sync` use this default when no folder argument is supplied. A positional
folder overrides it. Network shares must be readable by the account and execution
environment running NoTunes. An access failure aborts the scan. On Linux, supply
a Linux path or update the configuration accordingly; mount network shares first
and use their local mount path. Windows UNC paths are not mapped automatically.

## Architecture and dependency injection

| Project | Responsibility |
| --- | --- |
| `Source/NoTunes/NoTunes.csproj` | CLI entry point, DI composition, commands, source inventory and shared Apple protocols. |
| `Source/Platform.Abstractions/NoTunes.Platform.Abstractions.csproj` | `IUsbMuxTransport`, `ISourceFileSystem` and per-scan `ISourceSession` contracts. |
| `Source/Platform.Windows/NoTunes.Platform.Windows.csproj` | Apple Mobile Device Service TCP transport and Windows file-handle protection. |
| `Source/Platform.Linux/NoTunes.Platform.Linux.csproj` | usbmuxd Unix-socket transport and Linux descriptor-relative file access. |

All projects target `net10.0`. The CLI registers the current platform using
`Microsoft.Extensions.DependencyInjection` and resolves `CommandContext` and
`SourceScanner` through constructor injection. Every verb derives from
`CommandOptions` and implements its command directly in `Execute(context, token)`;
`Program` uses one common `MapResult` execution callback. `DeviceOptions` supplies
shared USB selection and timeout validation. Platform providers are singletons;
each scan owns and disposes its own source session. Command options, source scanning and
Apple protocols share the application assembly; platform selection stays in
`ApplicationServices`, and native implementations stay in the platform projects.
Unsupported operating systems produce a clear error at the composition boundary.

Each project has its own directory; no project directory contains another project.
The three platform projects are grouped under the `Platform` solution folder.
The application uses the SDK's default item discovery within `Source/NoTunes`.

Output is supplied through `TextWriter` instances: the entry point passes
`Console.Out` and `Console.Error` to `AddNoTunes`. Composition passes them to
commands and sync services; those services do not access the global console.
The command-line parser uses the same supplied output writer for help.
Tests can provide `StringWriter` or `TextWriter.Null` without redirecting global
process output.

`SyncEngineIntegrationTests` exercise the complete sync engine with real source
files, SQLite catalogues, durable journals and the AFC/ATC clients. An internal
connection factory substitutes a simulated device after pairing; the normal
connection factory is unchanged. The tests cover multiple addition batches before
deletions, artwork-only and mixed artwork updates, dry-run with pending recovery,
audio verification failures, interrupted transfers and interrupted recovery.
Pairing and TLS have separate protocol tests; the simulated receiver does not
replace physical-iPhone validation.

The copied catalogue is read with `System.Data.SQLite` 2.0.4 and the separate
native `SQLite` 3.53.4 package. This avoids the SQLitePCLRaw dependency chain.
The native package includes Windows and Linux binaries; both provider paths
have been exercised by the automated tests.

Within the core, `DeviceProbe` coordinates pairing, device validation, file-service
checks and optional catalogue capture. `AtcClient` owns ATC startup, Ping/Pong,
music metrics and strict sync-permission parsing, and returns an `AtcSyncStatus`.
Its startup sequence is covered by simulated binary-plist frames, including
the message limit, malformed responses, timeout, cancellation and stream disposal.
`ProbeResult` preserves the CLI's existing flat JSON output.

## Commands

```powershell
dotnet run --project Source/NoTunes/NoTunes.csproj -- --help
dotnet run --project Source/NoTunes/NoTunes.csproj -- devices

dotnet run --project Source/NoTunes/NoTunes.csproj -- init --dry-run

dotnet run --project Source/NoTunes/NoTunes.csproj -- init
dotnet run --project Source/NoTunes/NoTunes.csproj -- probe
dotnet run --project Source/NoTunes/NoTunes.csproj -- probe --read-catalog --json
dotnet run --project Source/NoTunes/NoTunes.csproj -- probe --device 00008030-0011223344556677 --timeout 30 --json
dotnet run --project Source/NoTunes/NoTunes.csproj -- scan
dotnet run --project Source/NoTunes/NoTunes.csproj -- scan "D:\Music"
dotnet run --project Source/NoTunes/NoTunes.csproj -- scan "D:\Music" --json
dotnet run --project Source/NoTunes/NoTunes.csproj -- sync "D:\Music" --dry-run --json
dotnet run --project Source/NoTunes/NoTunes.csproj -- sync "D:\Music"
```

After building, the equivalent executable is
`Source\NoTunes\bin\Debug\net10.0\NoTunes.exe`.

- `init [--dry-run] [--device <UDID>] [--state-folder <path>]` prepares an iPhone
  that has never been synced with a host music library. It creates a persistent
  library identity, writes the matching preference pair and sends a signed delta
  containing `update_db_info` and a hidden, empty library playlist. It verifies
  Media revision 1 by reading the device catalogue back. No audio is transferred
  and the phone's SQLite database is never edited directly. Existing libraries,
  foreign preferences and saved host catalogues are refused. Run `init` again
  after an interruption: it reuses its saved identities and verifies any partial
  preference writes before completing them. If initialization has already committed,
  a write run clears matching metadata staging remnants under the sync lock.
  A partial catalogue commit without
  the primary container or first Media revision is refused and requires manual
  reconciliation. `--dry-run` sends no sync request
  and writes no device files. The initial iOS database must exist; open Music on
  the phone first if iOS has not created it yet.
- `devices` lists USB devices through Apple's local USB multiplexer; network
  devices are excluded. It does not require a music library.
- `probe` validates the saved pairing, opens an encrypted lockdown session,
  verifies the iPhone's identity and opens AFC and ATC. It checks whether the
  music database exists and whether the phone permits music sync. It only reads
  device information and sends protocol pings; it never requests a media sync.
  With `--read-catalog`, it also reads the database and its WAL twice and checks
  that both contents and file attributes agree. These captures stay in memory.
- `scan` recursively enumerates `.mp3` files, including `.MP3`, and hashes their
  complete bytes using SHA-256. This is an inventory, not MP3 audio validation.
  An inaccessible folder, locked file, detected file change, symlink or junction
  aborts the scan. It does not silently treat unreadable data as a deleted track.
  Safe scanning requires read access to the source's parent folders.
  Paths through symlinks or junctions in parent folders are rejected too.
  Windows blocks directory renames and junction changes during a scan. Linux
  enumerates held directory descriptors and opens files relative to them with
  `openat` and `O_NOFOLLOW`, so replacing a pathname cannot redirect file reads.
  Size and modification time are checked on the opened file handle on both platforms.
- `sync [folder] [--dry-run] [--device <UDID>]` mirrors local MP3 files to
  computer-synced, local Music tracks. Cloud purchases, remote items, videos and
  other media types are preserved. Tracks are matched by their complete SHA-256
  hashes, including duplicate counts; a renamed file does not trigger a transfer.
  Replacing changed bytes uses an insertion followed by removal of the old track;
  it creates a new track identity. Unchanged tracks retain their identities.
  Embedded covers are registered with new tracks. Missing covers on byte-identical
  existing tracks are repaired with artwork-only updates that retain their audio,
  metadata and track identities. Dry-run lists these under `Artwork` in JSON or
  as `~ Cover:` entries in text output.

Device commands take `--timeout` (1–300 seconds per request) and `--json`.
With multiple USB devices, `probe` requires `--device`; it never chooses one
arbitrarily. Errors go to stderr. JSON results go to stdout.

### Synchronization and recovery

The first comparison can take time: candidate device files are read over USB and
hashed on the host. Only files whose size occurs in the source need hashing.
No device-side file-hash operation is used. Diagnostics go to stderr; `--json`
prints the plan, dry-run flag and verified batch count to stdout.

Before writes, all new source files are copied to a private temporary directory,
checked against the scan hashes and read with TagLibSharp. Source access continues
to use the platform's directory and file guards. This requires temporary disk
space for the additions and any missing-cover repairs. Tracks without embedded
covers are checked through a guarded read-only tag stream without making a copy.
Invalid or changed source files abort before metadata
is staged. The iPhone must have room for all additions plus a metadata reserve;
pending deletions are not counted as available space.

The catalogue supports at most 50,000 computer-synced local music tracks.
Write runs check the full intermediate inventory before staging anything:
existing tracks plus all additions must fit this limit, because additions are
verified before deletions. A replacement at the limit is refused before device
changes; `--dry-run` can still display its plan. Preserved cloud, remote and
nonmusic items do not count against this limit.

Each batch uses at most 499 track changes and 4 MiB of serialized operation
metadata, the shared Apple AFC sync lock and a fresh
paired ATC connection. During an active session, ATC keepalive pings run every
15 seconds, including during durable journal copies and AFC read-back.
The saved library identity and current catalogue must
still match the plan, including existing album and album-artist group identities.
The live `ReadyForSync` Media anchor must match too; an advertised VoiceMemo
anchor must be the supported value 1. A signed
binary operation file and its `.cig` sidecar are staged and read back before
metadata completion is announced. Audio is streamed in bounded chunks, read
back, renamed in the Airlock and completed through ATC. ATC messages are processed
throughout the transfer: pings are answered and failures or revoked permissions
cancel the upload. Only expected audio and known ancillary-only asset requests
are accepted. The host never edits the
phone's SQLite database or directly deletes its audio files.

Success requires the current session's `SyncFinished`, the next catalogue
revision, exactly the expected track inventory and byte-identical read-back of
new permanent audio files. Protected purchases, cloud, remote and nonmusic
entries are captured separately, including entries without a sync identity or
complete audio metadata. Their identities and captured metadata must remain
unchanged before and after every batch. Existing groups retain their identities;
empty groups may be removed by the receiver after track deletion.
Additions finish and are verified before any removal
batch begins. Apple's receiver may reset recent play/skip counters during a
normal sync; byte matching preserves existing track identities, not those
receiver-maintained recent counters.

Operation journals are stored under the user's local application data in
`NoTunes`, or under `--state-folder <path>`. The exact operation and signature
are flushed before staging. Version 2 journals also retain the before-catalogue,
prepared metadata and verified durable audio/JPEG copies in private state storage.
Journal metadata is limited to 256 MiB before any device operation is staged;
audio and JPEG data are kept in separate files. If preparing these copies or saving
their journal fails, the unjournaled copies are removed; an existing pending batch
retains its recovery evidence.
A failure after journal creation leaves `pending.json`. The next write run reconciles that batch before
planning new changes: unapplied operations are archived, completed results are
verified, and only that batch's unfilled insertions are removed through a new signed
compensating session before the source plan retries them. Interrupted artwork-only
updates retry their cover registration without deleting the existing track.
Compensating deletions of incomplete tracks remain recoverable across further
interruptions, including partially applied cleanup batches.
Partially applied removals are reconciled against the observed inventory; the
fresh source plan removes only the entries that are still present.
An uncertain operation is never blindly replayed. Only staging files equal to the
recorded bytes or a recorded write prefix may be removed. Metadata staging is
checked and cleared before requesting a new session, because the receiver can
delete leftover operation plists during session preparation. Unexpected revisions,
foreign staging data, changed library identities and unverifiable outcomes stop
recovery and retain its record. Old version 1 journals lack the required evidence
and still require manual reconciliation. Verified batches remain recorded; private
asset copies are discarded after settlement. A multi-batch mirror is not atomic.
`--dry-run` remains read-only and does not perform recovery.

If an interrupted transfer left catalogue entries without audio, the dry-run
lists them as incomplete. JSON entries have `Path: null` and `IsIncomplete: true`.
Write runs refuse such a catalogue until the interrupted sync has been reconciled.

An empty source requires `--allow-empty-source` for a write run. A source access
error aborts the scan and cannot be interpreted as an empty source.

Use `init` once for a new library; subsequent `sync` commands use its saved
identity and `db_info`. Existing libraries continue to use the observed saved
iTunesCDB formats 115 and 117 with VoiceMemo anchor 1, and initial Media anchor 0
when a legacy library has no ATC anchor yet.

Artwork uses a front-cover image when available, otherwise the first embedded
picture. JPEG bytes are retained; PNG and other formats supported by the managed
stb decoder are converted to JPEG with transparency flattened onto white. Images
are bounded to 32 MiB and 16 million pixels and validated before staging. Cache
IDs are allocated above live local artwork tokens and the saved ArtworkDB counter
and image IDs. Covers are staged and read back separately, then verified against
the track's registered token, preferred artwork token and permanent JPEG original.
The source MP3 is unchanged. The image codecs and their licenses are documented in
`THIRD-PARTY-NOTICES.md`.

Compilation flags and explicit album artists are retained. A compilation without
an album artist is rejected during source preparation, before device writes;
set the album artist in its MP3 tags first. Existing album, artist and composer
sort names are preserved when new tracks join their groups. ASCII case variants
reuse group identities; names containing non-ASCII characters require an exact
match in each album or artist name.

## Device prerequisites

### Windows

Apple device support and Apple Mobile Device Service must be installed and
running on Windows. The application connects to the service at
`127.0.0.1:27015`; it does not bundle Apple's drivers or DLLs. The iTunes UI is
not used. Installation of device support and the initial trust relationship can
be handled by Apple's Windows device software.

### Linux (automated tests passed; physical-device validation pending)

Install and start [usbmuxd](https://github.com/libimobiledevice/usbmuxd) using your
distribution's packages. NoTunes connects to its Unix socket at
`/var/run/usbmuxd`; the user running NoTunes must have access to this socket.
Use your distribution's libimobiledevice pairing tools to establish trust first,
for example [`idevicepair pair`](https://github.com/libimobiledevice/libimobiledevice/blob/master/docs/idevicepair.1).
NoTunes reads pairing records through usbmuxd instead of opening its private
pairing files directly.

The Linux source provider supports x64 and ARM64. It requires readable procfs at
`/proc/self/fd`, libc support for `openat`, `statx` and `flock`, and a filesystem
that supports the requested metadata and advisory file locks. It opens each path
component without following symbolic links and rejects non-regular MP3 entries.
Linux file locks are advisory; directory descriptors preserve the selected
directory identity instead of preventing directory renames.
The scanner traverses directories depth first and releases completed Linux
directory descriptors, so descriptor usage grows with nesting depth rather than
the total number of album folders.

```bash
dotnet run --project Source/NoTunes/NoTunes.csproj -- devices
dotnet run --project Source/NoTunes/NoTunes.csproj -- scan /mnt/music --json
dotnet run --project Source/NoTunes/NoTunes.csproj -- probe --read-catalog --json
```

Automated tests have passed on Windows and in an x64 Linux Docker container with
.NET 10. This includes native Linux filesystem regressions for symlink rejection,
directory renames and bounded descriptor usage, as well as a simulated usbmuxd
peer over local Unix sockets and DI registration tests.

**Linux support has not been tested with a real usbmuxd daemon or an iPhone
connected over USB.** ARM64 execution is also unverified. Container tests do not
validate physical USB connectivity or replace real-device checks.

### Pairing and device validation

Connect the iPhone over USB, unlock it and trust the computer. The application
uses the existing pairing record; it does not create or replace pairings.
The lockdown session requires TLS and verifies the device certificate against
that record before allowing service access. Pairing certificates
and private keys are never included in diagnostic output or JSON.

The target is iOS 27. On 16 November 2026, the merged Windows application was
tested successfully on an iPhone SE (2nd generation, `iPhone12,8`) running
iOS 27.2.1 (24B156), with 1,885 tracks. USB discovery, pairing, pinned TLS, AFC,
ATC startup and two matching catalogue captures passed. The database contained
16,486,400 bytes and its WAL contained 4,115,912 bytes. No music was transferred
or deleted; this validates the transport and read path.

The write path was subsequently tested on the same phone: one generated MP3
test title was inserted, verified against the original SHA-256 bytes at its
permanent path, and removed through a second signed delta session. The catalogue
advanced from Media anchor 0 to 1 and then 2, and returned from 1,886 to the same
1,885 original track entries. No existing track was deleted. A full mirror of
the configured network share has not been run. Regression tests cover the
phone's missing initial ATC anchor and its older iTunesCDB format 115.

Apple pairing certificates may have an empty issuer name. BouncyCastle 2.7
[rejects that form during ordinary X.509 parsing](https://github.com/bcgit/bc-csharp/blob/release-2.7.0/crypto/src/asn1/x509/TBSCertificateStructure.cs).
NoTunes uses a compatibility
view only for certificates already present in the saved pairing record; TLS
transmission and exact certificate pinning retain the original DER bytes.
ASN.1 depth limits remain enabled. Regression tests cover empty issuers, TLS
authentication, unchanged certificate bytes and rejection of mismatched pins.

## Remaining work

Playlist synchronization and detailed MP3 gapless timing remain unimplemented.
Initialization, recovery and artwork registration have automated simulation tests
on Windows and Linux; these new paths still require physical-iPhone validation.
Syncing a NoTunes-initialized library with Apple's host software may replace the
music that NoTunes transferred, so keep the source music and persistent state.

The iPhone catalogue must be updated through its sync service, never by editing
`MediaLibrary.sqlitedb` directly. Documentation used for the protocol boundary:

- [libusbmuxd](https://github.com/libimobiledevice/libusbmuxd)
- [usbmuxd daemon and Unix-socket interface](https://github.com/libimobiledevice/usbmuxd)
- [Linux descriptor-relative file access](https://man7.org/linux/man-pages/man2/open.2.html)
- [Linux statx metadata](https://man7.org/linux/man-pages/man2/statx.2.html)
- [libimobiledevice lockdown and AFC](https://github.com/libimobiledevice/libimobiledevice)
- [ATC protocol observations](https://github.com/fbiba/rustunes/blob/master/docs/02-atc-sync-protocol.md)
- [USB transport observations](https://github.com/fbiba/rustunes/blob/master/docs/03-transport-usb-and-wifi.md)

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Command succeeded, or help/version requested |
| 1 | Source, device, transport or protocol error |
| 2 | Invalid command-line syntax |
| 3 | Probe completed, but the iPhone blocks music synchronization |
| 130 | Cancelled with Ctrl+C |
