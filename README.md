# NoTunes

A .NET 10 command-line application with Windows and Linux platform providers.
The intended behavior is to mirror
all MP3 files in a source folder and its subfolders to the iPhone's built-in Music
app over USB or Wi-Fi. No GUI, playlists, transcoding, WSL or external sync executable.

`sync` mirrors MP3 files to an existing, initialized iPhone Music library over
USB or Wi-Fi. It preserves byte-identical tracks, transfers additions through AFC and
removes obsolete computer-synced music through signed ATC delta sessions.
`sync --dry-run` reads the source and device and prints the actual change plan
without starting a write session.

## Build and test

```powershell
dotnet build NoTunes.slnx
dotnet test Tests/NoTunes.Tests.csproj
```

Supported native runtime variants are `win-x86`, `win-x64`, `win-arm64`,
`linux-x64` and `linux-arm64`. A build without `-r` includes only these native
variants, plus shared Windows/Linux runtime assets. The filter also applies to
test and publish output; other explicit runtime identifiers are rejected.
Linux targets use glibc; musl/Alpine and 32-bit Linux are outside this support set.

For a smaller output containing just one native variant, specify its runtime:

```powershell
dotnet build Source/NoTunes/NoTunes.csproj -r win-x64 --no-self-contained
dotnet publish Source/NoTunes/NoTunes.csproj -c Release -r linux-x64 --self-contained false
```

The runtime-specific build is under `bin/Debug/net10.0/<RID>/`; the publish
output is under `bin/Release/net10.0/<RID>/publish/`. These framework-dependent
outputs require the matching .NET 10 runtime on the destination machine.

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

dotnet run --project Source/NoTunes/NoTunes.csproj -- remove-cover "Seal - Amazing.mp3"

dotnet run --project Source/NoTunes/NoTunes.csproj -- remove-cover "D:\Music\song.mp3" --json
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
- `devices` lists USB devices through Apple's local USB multiplexer, filtered by
  the supplied or configured UDID. `devices --wifi` describes the configured
  direct target, or lists known network devices when no address is configured.
  USB remains the default; choosing Wi-Fi never silently falls back to USB.
  It does not require a music library. `devices --address <IP> --device <UDID>`
  describes the supplied target without checking its reachability.
- `probe` validates the saved pairing, opens an encrypted lockdown session,
  verifies the iPhone's identity and opens AFC and ATC. It checks whether the
  music database exists and whether the phone permits music sync. It only reads
  device information and sends protocol pings; it never requests a media sync.
  With `--read-catalog`, it also reads the database and its WAL twice and checks
  that both contents and file attributes agree. These captures stay in memory.
- `scan` recursively enumerates `.mp3` files, including `.MP3`, and hashes their
  complete bytes using SHA-256. This is an inventory, not MP3 audio validation.
  `scan` and `sync` persist source hashes in the user's local NoTunes data folder:
  `%LOCALAPPDATA%\NoTunes\hash-cache` on Windows and
  `$XDG_DATA_HOME/NoTunes/hash-cache` (normally `~/.local/share/NoTunes/hash-cache`)
  on Linux. The .NET local application data API resolves the platform-specific path.
  Subsequent runs reuse hashes when the source root, relative path, size and UTC
  modification time match, avoiding full reads of unchanged source files over the
  network. Replacing bytes while preserving both size and modification time is
  assumed not to occur. Delete the cache folder to force a full source hash scan.
  Cache files are replaced atomically after successful scans; unreadable or corrupt
  caches are rebuilt, and cache write failures produce diagnostics without aborting
  the scan. `--state-folder` controls recovery journals, not this source cache.
  An inaccessible folder, locked file, detected file change, symlink or junction
  aborts the scan. It does not silently treat unreadable data as a deleted track.
  Safe scanning requires read access to the source's parent folders.
  Paths through symlinks or junctions in parent folders are rejected too.
  Windows blocks directory renames and junction changes during a scan. Linux
  enumerates held directory descriptors and opens files relative to them with
  `openat` and `O_NOFOLLOW`, so replacing a pathname cannot redirect file reads.
  Size and modification time are checked on the opened file handle on both platforms.
- `sync [folder] [--dry-run] [--repair-covers] [--device <UDID>]` mirrors local MP3 files to
  computer-synced, local Music tracks. Cloud purchases, remote items, videos and
  other media types are preserved. Tracks are matched by their complete SHA-256
  hashes, including duplicate counts; a renamed file does not trigger a transfer.
  Replacing changed bytes uses an insertion followed by removal of the old track;
  it creates a new track identity. Unchanged tracks retain their identities.
  Embedded covers are registered with new tracks. Existing tracks' covers are
  checked only with `--repair-covers`, which repairs missing covers on byte-identical
  tracks with artwork-only updates that retain their audio, metadata and track
  identities. Use `sync [folder] --repair-covers --dry-run` to preview these repairs
  under `Artwork` in JSON or as `~ Cover:` entries in text output.
- `remove-cover <file> [--folder <path>] [--json]` removes all embedded pictures
  from exactly one local MP3, including pictures that cannot be decoded. Absolute
  paths select the file directly; relative paths resolve below `MusicSourceFolder`
  in `app.config`, or below the explicit `--folder`. No iPhone is required or
  contacted and there is no confirmation prompt. It preserves the MPEG audio
  bytes and the other tags. A file with no pictures is left unchanged and succeeds
  with a zero removal count. JSON reports `Path` and `RemovedCovers`.
  Editing uses the platform's guarded file handles and an exclusive edit lock.
  A temporary original is retained until the edited file has been reopened and
  its cover removal and unchanged audio hash verified. On a write or verification
  failure, the original bytes are restored; if restoration itself fails, the
  error reports the retained backup path. Successful edits change the file's
  modification time so the next source scan refreshes its cached hash.
  The command does not change synchronization's handling of invalid covers or
  modify the iPhone. Because synchronization compares complete file hashes,
  the changed MP3 can be transferred as a replacement on the next write sync.

Device commands take `--timeout` (1–300 seconds per request) and `--json`.
`DeviceUdid` in `app.config` selects the default iPhone. With multiple devices and
no configured default, `probe` requires `--device`; it never chooses one arbitrarily.
Errors go to stderr. JSON results go to stdout.

### Wi-Fi synchronization

Pair the iPhone over USB first: unlock it and trust this computer. With USB still
connected, run `enable-wifi` once to enable `EnableWifiConnections` in the phone's
wireless lockdown settings. This verifies the saved pairing and reads the setting
back after writing it. It does not enable wireless debugging or change the music
library. Apple Devices/iTunes' Wi-Fi synchronization setting is an alternative.

```powershell
dotnet run --project Source/NoTunes/NoTunes.csproj -- enable-wifi
dotnet run --project Source/NoTunes/NoTunes.csproj -- devices --wifi
dotnet run --project Source/NoTunes/NoTunes.csproj -- sync --wifi --dry-run
```

`app.config` supplies the default `DeviceUdid=00008030-00112CE23A86402E` and leaves
`DeviceAddress` blank for automatic discovery. Device commands use the configured
UDID when `--device` is omitted, so `sync --wifi --dry-run` is sufficient for this
iPhone when it advertises Wi-Fi sync. A nonblank `DeviceAddress` selects a direct
connection instead. Explicit `--device` and `--address` arguments override their
corresponding defaults independently.
Without `--wifi` or an explicit `--address`, USB remains the selected transport.

When no address is configured or supplied, Wi-Fi selection first uses network
devices published by the local Apple service/usbmuxd. If the selected UDID is
missing, NoTunes falls back to its own Bonjour/mDNS browser for
`_apple-mobdev2._tcp.local`. This fallback requires `--device` or `DeviceUdid` to
choose the trusted pairing. It does not infer identity from a device name or MAC
address: discovered candidates must authenticate with the saved pairing certificate
and return the matching UDID inside the encrypted session. Other devices and stale
addresses are skipped. It does not create new pairings or change music.

The built-in browser follows [RFC 6762's legacy query behavior](https://www.rfc-editor.org/rfc/rfc6762#section-6.7)
and sends mDNS queries from ephemeral UDP ports on active
multicast-capable IPv4/IPv6 interfaces; no Bonjour installation or exclusive use of
UDP port 5353 is needed. It collects responses for up to three seconds, resolves
split PTR/SRV/A/AAAA answers, and retains interface scopes for link-local IPv6.
Bonjour supplies candidate addresses; the advertised service port need not be
lockdown's port. Pairing verification and subsequent lockdown connections use
the standard port 62078.
Candidate checks take up to three seconds each within an overall authentication
budget set by `--timeout`. The iPhone must be awake and advertise Wi-Fi sync, and
the network/firewall must allow mDNS multicast and its unicast responses.

`--address` implies Wi-Fi and requires either `--device` or a configured
`DeviceUdid`. It accepts IPv4 and IPv6, including scoped link-local addresses.
Both devices must be on a network that permits direct connections.
`devices --wifi --json` authenticates Bonjour-discovered targets, while a configured
direct target is listed without checking reachability; use `probe` to verify it.

```powershell
dotnet run --project Source/NoTunes/NoTunes.csproj -- probe --address 192.168.0.42 --device 00008030-00112CE23A86402E --read-catalog
dotnet run --project Source/NoTunes/NoTunes.csproj -- sync --address 192.168.0.42 --device 00008030-00112CE23A86402E --dry-run
dotnet run --project Source/NoTunes/NoTunes.csproj -- sync --address 192.168.0.42 --device 00008030-00112CE23A86402E
```

`probe`, `init` and `sync` support the same connection options. Lockdown and all
device services connect directly over TCP on Wi-Fi and use TLS with the saved
pairing certificate. The phone's UDID is checked inside the encrypted session.
The local multiplexer is still needed to retrieve the pairing record; no pairing
keys are exported or printed. Journals and verified hash caches keep the same
device identity when switching between USB and Wi-Fi.

Unlock the phone when connecting. One heartbeat and a renewable wireless power
assertion stay open for the entire command, including all batches and reconnects.
An initial six-second settling period detects an already pending sleep before
the device operation starts. Sleep notifications, lost heartbeats or failed power
assertion renewals cancel the operation; write outcomes continue to use the existing
recovery journal. A per-device local lock excludes overlapping NoTunes Wi-Fi commands,
including probes and dry-runs, even with different `--state-folder` values.
Other Apple synchronization programs should finish before starting NoTunes.
NoTunes does not automatically wake a sleeping phone. Automatic discovery runs for
each command; an address change during a running command still requires a retry.

### Synchronization and recovery

Source discovery and hashing run alongside iPhone connection setup, verified
catalogue capture and file-attribute reads. When source sizes are not yet known,
the attribute read covers all complete local iPhone tracks. Audio hashing still
uses the completed source inventory to restrict reads to matching file sizes.
Comparison and source preparation wait for both inventories. If either side
fails, the other is cancelled and awaited before the command returns; no new
write session starts with an incomplete source. Recovery of a pending write also
waits for a successful source scan before it can modify the phone.
Interactive progress combines overlapping operations on the current console line;
redirected diagnostics retain complete individual progress lines.

Each comparison can take time: candidate device files are read over the selected connection and
hashed on the host. Only files whose size occurs in the source need hashing.
Verified device hashes are cached under the local NoTunes data folder in
`device-hash-cache`, separately from source hashes and recovery journals.
`--state-folder` does not relocate either hash cache. Cache entries belong to one
device and saved library/database/container identity, and match the persistent
track ID, path, size and all stable AFC file attributes, including modification
time. Access time is ignored. Missing or unusable modification times disable reuse.
Candidate file attributes are queried in batches of at most 64 requests; audio
reads and writes remain sequential. A missing file cannot become a cache hit.
Cache files are validated, bounded in size and replaced atomically after a
successful comparison. Invalid caches trigger full reads; cache persistence
failures are reported and do not abort synchronization.

`sync --full-readback` bypasses cached device hashes and refreshes them from full
USB reads of every comparison candidate. The default assumes content changes are
reflected in file attributes; replacing bytes while preserving every stable
attribute is detected by a full readback. The source hash cache is independent
and remains enabled. Newly transferred audio and separate artwork are always
read back and verified regardless of cache hits or this option.
Embedded pictures participate in the MP3 hash. Separate registered iPhone artwork
is not audited by `--full-readback`; checking and repairing missing covers on
existing tracks requires `--repair-covers` independently of this option.
No device-side file-hash operation is used. Diagnostics include cache-hit/read
counts on stderr; `--json` prints the plan, dry-run flag and verified batch count
to stdout.

`scan` and `sync` report percentage progress to stderr for each work phase:
source hashing, catalogue reads, device comparison, source and cover preparation,
asset transfers, verification and recovery. Transfer progress displays data sizes
in B/KB/MB/GB (and larger units), with one decimal place for KB and above. Units
use decimal multiples: 1 KB = 1,000 bytes. Other phases use file or read counts
with comma thousands separators. Each percentage is printed once per
phase. In an interactive terminal, updates replace the current progress line;
completion, errors and debug messages finish that line. Redirected stderr keeps
ordinary log lines without cursor-control characters.
These are phase percentages, not an estimated percentage for the entire sync.
Source discovery precedes hashing because the number of files is initially unknown.
It enumerates paths without reading audio; hashing then rechecks the inventory
through the existing file guards. An inventory change aborts the scan.
Atomic device deletions show 0% while pending and 100% after verification.
Use `sync --debug` to also print per-track `Compare:` and `Transfer:` diagnostics.
Progress and debug output do not change the JSON result on stdout.

Sync catalogue checks retry transient read inconsistencies up to five times.
Every successful check still requires two matching database/WAL captures and
stable file attributes. Retries repeat reads, never signed sync operations.

Before writes, all new source files are copied to a private workspace,
checked against the scan hashes and read with TagLibSharp. Copying and hash verification
of the next file overlap metadata and cover processing of the current file. Only one
file is prefetched; metadata processing, group prompts and ID allocation remain ordered.
Source access continues to use the platform's directory and file guards. A failure or
cancellation stops and joins the outstanding copy before closing its source session.
This requires workspace disk space for the additions and any missing-cover repairs
requested with `--repair-covers`.
Write runs create the workspace on the recovery journal's volume. Verified MP3 copies
are moved into each batch's durable assets instead of copied a second time. They are
flushed and hash-checked before publishing the journal, and transfers use those durable
paths. Dry runs use the system temporary directory. Abandoned preparation workspaces
are cleaned on the next write run while holding the exclusive device lease.
Existing tracks are not read for cover preparation by default. Tracks without embedded
covers are checked through a guarded read-only tag stream without making a copy.
Validated JPEGs are also stored in the workspace and loaded as needed,
so allow disk space for these cover files as well. A bounded content cache reuses
decoding and conversion for identical embedded images while retaining distinct
track artwork IDs.
These shared cover files are still copied into each track's recovery assets.
Files with unreadable or unsupported MP3 metadata or invalid embedded covers are
reported individually and skipped. Valid additions and cover repairs continue.
If any file is skipped, **all planned deletions are deferred for that run** so an
older iPhone copy is preserved when its source replacement could not be prepared.
This also applies when every planned addition is skipped; it never becomes an
empty-source deletion. The next error-free run can perform the deferred deletions.
The actual plan excludes skipped additions and repairs and exposes `Skipped`
(path, reason and whether only a cover repair was affected) and `DeferredDeletions`
in JSON. Text output includes their counts, with individual diagnostics on stderr.
Both dry-runs and write runs return exit code 5 when files were skipped, indicating
a partial result rather than a complete mirror.

Source access failures, files changed after scanning, unresolved group selections,
cancellation, device/protocol failures and unsuccessful transfer verification
still stop the run. These errors are not treated as defective MP3 metadata.
The iPhone must have room for all valid prepared additions and covers plus a metadata reserve;
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
Recovery also checks album and artist rows created before audio arrived. A changed
group sync ID is accepted only for the same internal row with unchanged names,
sorting and membership, and only when the signed request specified that new ID.
Retained tracks may reflect that proven change in their derived group sync ID;
unrelated metadata changes remain errors. Receiver spelling variants additionally
require the recorded insertion's actual internal group association. The original
signed operation and journal are never rewritten to accommodate these differences.
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
and image IDs. Saved ArtworkDB versions 0.2 and 0.6 are supported, including
image IDs shared by multiple track records. Covers are staged and read back
separately, then verified against
the track's registered token, preferred artwork token and permanent JPEG original.
The source MP3 is unchanged. The image codecs and their licenses are documented in
`THIRD-PARTY-NOTICES.md`.

ATC asset manifests may include cover or Genius requests for existing tracks as
well as the current additions or artwork updates. Their entry limit is the number
of distinct track IDs in those sets, rather than a fixed 1,024 entries. Every entry
is validated before any asset is uploaded; unknown IDs, duplicate IDs and unexpected
asset parts are rejected. Device messages remain bounded to 16 MiB. Manifest errors
report unsupported keys, unexpected Media value types or counts exceeding the
expected number of distinct tracks.

Compilation flags and explicit album artists are retained. A compilation without
an album artist is rejected during source preparation, before device writes;
set the album artist in its MP3 tags first. Existing album, artist and composer
sort names are preserved when new tracks join their groups. ASCII letters are
compared without case even inside Unicode names. If no exact group exists, a
unique receiver spelling that differs only by case or diacritics is reused, such
as `Celine Dion` / `Céline Dion`. Multiple possible groups require an interactive
selection; source MP3 tags remain unchanged.

Existing album and album-artist rows may share a nonzero host sync ID, including
rows with different name spellings. NoTunes reads those rows separately by their
internal database IDs and preserves existing track assignments. Audio additions
reuse the receiver's first internal row for a shared sync ID only when the source
name uniquely identifies a row and the spellings differ solely by periods or
ASCII case, such as `Boney M` / `Boney M.` and `REM` / `R.E.M.`. Album aliases
must also have the same album-artist row. Otherwise the run asks for a different
unambiguous name. Inserted metadata uses the receiver's existing name and sort
name; source tags and existing groups remain unchanged. Verification still checks
the exact internal target row, names, sort names and sync IDs.

Recovery applies the same interpretation to old signed requests using only the
group rows captured before that operation. It does not rewrite the signed request
or accept an arbitrary row with the same sync ID. Millisecond verification accepts
the value derived from rounded samples or the submitted value when they differ
by at most 1 ms; sample counts and all other audio properties remain exact.

If a name identifies multiple rows or the sort name is ambiguous, an interactive
run asks for an unambiguous existing target name or a distinct name for a new
group. The question includes the affected source file and conflicting entries.
The answer applies to that source group for the current run. Empty input cancels;
Ctrl+C also cancels while waiting for an answer. With redirected standard input,
the CLI reports the conflict instead of waiting for interactive input. Questions
go to stderr, so `--json` results remain valid JSON. Dry-run performs the same
resolution without staging device changes. Choices affect the inserted metadata,
not the source file's tags or existing track assignments. NoTunes does not merge,
rename or re-key existing groups. Journals retain individual group rows so
recovery and batch verification can detect identity or membership changes.
Older checkpoints without that evidence remain usable with unambiguous group
sync IDs. Artwork-only updates do not require resolving audio insertion groups.

## Device prerequisites

### Windows

Apple device support and Apple Mobile Device Service must be installed and
running on Windows. The application connects to the service at
`127.0.0.1:27015`; it does not bundle Apple's drivers or DLLs. The iTunes UI is
not used. Installation of device support and the initial trust relationship can
be handled by Apple's Windows device software.

[Apple Devices](https://apps.microsoft.com/detail/9np83lwlpz9k) supports x64
and ARM64; the ARM64 package requires Windows 11 24H2 or later. Apple's
[iTunes downloads](https://www.apple.com/itunes/) also provide x86 device
support. The runtime set was checked against these sources on 18 November 2026.
Only Windows x64 has been exercised on a physical iPhone; Windows x86 and ARM64
remain unverified with hardware.

### Linux (automated tests passed; physical-device validation pending)

Install and start [usbmuxd](https://github.com/libimobiledevice/usbmuxd) using your
distribution's packages. NoTunes connects to its Unix socket at
`/var/run/usbmuxd`; the user running NoTunes must have access to this socket.
Use your distribution's libimobiledevice pairing tools to establish trust first,
for example [`idevicepair pair`](https://github.com/libimobiledevice/libimobiledevice/blob/master/docs/idevicepair.1).
NoTunes reads pairing records through usbmuxd instead of opening its private
pairing files directly.

The Linux source provider supports x64 and ARM64 on glibc distributions.
[Debian's usbmuxd package](https://packages.debian.org/trixie/usbmuxd) is available
for both architectures. Linux uses usbmuxd/libimobiledevice instead of an
Apple-supplied Windows driver. The source provider requires readable procfs at
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

On 17 November 2026 the compatibility changes were tested on that phone's
existing library, including shared album/album-artist sync IDs, its actual
`best_artwork_token` schema and saved ArtworkDB version 0.2. One independent
synthetic MP3 was inserted and verified by SHA-256, then removed. The Media anchor
advanced from 2 to 3 and 4. A changing catalogue interrupted deletion verification;
the saved journal was reconciled after stable captures, without replaying the
deletion or requesting a new media import. The final comparison preserved all
1,885 original local tracks, 189 protected entries and every original album and
album-artist row, including their metadata and track assignments. No test title
or pending test journal remained. No artwork was uploaded in this physical test.
iOS retained one additional empty album row named `NoTunes USB verification`;
no track references it. That receiver-managed row was not deleted directly.

On 18 November 2026, Wi-Fi synchronization was tested on the same iPhone at
`192.168.0.42`. Enabling `EnableWifiConnections` over the existing USB pairing
allowed direct TCP lockdown connections; wireless debugging remained disabled.
The Wi-Fi probe passed pinned TLS, AFC, ATC and matching catalogue captures
(16,486,400 database bytes and 2,430,832 WAL bytes). A full Wi-Fi dry-run against
the configured music share completed with 1,908 unchanged tracks at Media anchor
9, no additions, deletions, skipped files or cover repairs, and zero write batches.
The cover check covered 1,230 candidates. The cable remained attached for this
test, but the selected device streams connected to the phone's Wi-Fi IP directly;
the multiplexer supplied only the saved pairing record. This validates the Wi-Fi
read path and keep-alive; no music write over Wi-Fi was exercised on the phone.

Apple pairing certificates may have an empty issuer name. BouncyCastle 2.7
[rejects that form during ordinary X.509 parsing](https://github.com/bcgit/bc-csharp/blob/release-2.7.0/crypto/src/asn1/x509/TBSCertificateStructure.cs).
NoTunes uses a compatibility
view only for certificates already present in the saved pairing record; TLS
transmission and exact certificate pinning retain the original DER bytes.
ASN.1 depth limits remain enabled. Regression tests cover empty issuers, TLS
authentication, unchanged certificate bytes and rejection of mismatched pins.

## Remaining work

Playlist synchronization and detailed MP3 gapless timing remain unimplemented.
Initialization, compensating recovery and artwork registration have automated
simulation tests on Windows and Linux; these paths still require physical-iPhone
validation. Reconciliation of an already committed deletion was exercised on the
physical device as described above.
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
- [USB and Wi-Fi transport observations](https://github.com/fbiba/rustunes/blob/master/docs/03-transport-usb-and-wifi.md)

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Command succeeded, or help/version requested |
| 1 | Source, device, transport or protocol error |
| 2 | Invalid command-line syntax |
| 3 | Probe completed, but the iPhone blocks music synchronization |
| 5 | Partial sync or dry-run: source files skipped; planned deletions deferred |
| 130 | Cancelled with Ctrl+C |
