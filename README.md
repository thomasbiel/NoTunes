# NoTunes

A .NET 10 command-line application with Windows and Linux platform providers.
The intended behavior is to mirror
all MP3 files in a source folder and its subfolders to the iPhone's built-in Music
app over USB. No GUI, playlists, transcoding, WSL or external sync executable.

**Current status: native USB transport prototype. Music transfer and deletion are
not implemented yet. `sync`, including `--dry-run`, returns exit code 4 and does
not claim to have synchronized anything.**

## Build and test

```powershell
dotnet build NoTunes.slnx
dotnet test Tests/NoTunes.Tests.csproj
```

CLI parsing uses `CommandLineParser` 2.9.1. Tests use NUnit 4.4.0 and the NUnit
Visual Studio test adapter. All application output and help text are in English.
TLS uses the managed `BouncyCastle.Cryptography` library. Pairing private keys
stay in memory; no Windows key containers or trusted certificates are installed.

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
`Microsoft.Extensions.DependencyInjection` and resolves `Commands` and
`SourceScanner` through constructor injection. Platform providers are singletons;
each scan owns and disposes its own source session. Commands, source scanning and
Apple protocols share the application assembly; platform selection stays in
`ApplicationServices`, and native implementations stay in the platform projects.
Unsupported operating systems produce a clear error at the composition boundary.

Each project has its own directory; no project directory contains another project.
The three platform projects are grouped under the `Platform` solution folder.
The application uses the SDK's default item discovery within `Source/NoTunes`.

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
dotnet run --project Source/NoTunes/NoTunes.csproj -- probe
dotnet run --project Source/NoTunes/NoTunes.csproj -- probe --read-catalog --json
dotnet run --project Source/NoTunes/NoTunes.csproj -- probe --device 00008030-0011223344556677 --timeout 30 --json
dotnet run --project Source/NoTunes/NoTunes.csproj -- scan
dotnet run --project Source/NoTunes/NoTunes.csproj -- scan "D:\Music"
dotnet run --project Source/NoTunes/NoTunes.csproj -- scan "D:\Music" --json
```

After building, the equivalent executable is
`Source\NoTunes\bin\Debug\net10.0\NoTunes.exe`.

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
- `sync [folder] [--dry-run] [--device <UDID>]` is reserved for the upcoming native
  synchronization engine. It currently validates the source and exits with a
  clear unsupported-operation message.

Device commands take `--timeout` (1–300 seconds per request) and `--json`.
With multiple USB devices, `probe` requires `--device`; it never chooses one
arbitrarily. Errors go to stderr. JSON results go to stdout.

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

### Pairing and previous device validation

Connect the iPhone over USB, unlock it and trust the computer. The prototype
uses the existing pairing record; it does not create or replace pairings.
The lockdown session requires TLS and verifies the device certificate against
that record before allowing service access. Pairing certificates
and private keys are never included in diagnostic output or JSON.

The target is iOS 27. Before the platform separation, the Windows read-only probe,
pairing, TLS, AFC, ATC startup and
verified catalogue capture were tested successfully on an iPhone SE (2nd
generation, `iPhone12,8`) running iOS 27.2 (24B119), with 1,885 tracks in its
library. This verifies the transport and read path, not music transfers.

## Remaining work

1. Parse the verified catalogue snapshots and implement library identity, live
   anchors and source-to-device matching.
2. Implement signed ATC sessions, ID3/audio metadata and AFC asset transfers.
3. Add a persistent operation journal and read-back verification before treating
   additions, replacements or removals as complete.
4. Enable `sync --dry-run` with a real device change plan and then enable writes.

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
| 4 | Music synchronization is not implemented |
| 130 | Cancelled with Ctrl+C |
