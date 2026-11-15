# NoTunes

A .NET 10 command-line application for Windows. The intended behavior is to mirror
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

The application project is `Source/NoTunes.csproj`; the NUnit project is
`Tests/NoTunes.Tests.csproj`. Both are included directly in `NoTunes.slnx`.

The default source is set in `Source/app.config`:

```xml
<configuration>
  <appSettings>
    <add key="MusicSourceFolder" value="\\192.168.0.150\music" />
  </appSettings>
</configuration>
```

The build copies this file to `NoTunes.dll.config` beside the executable. `scan`
and `sync` use this default when no folder argument is supplied. A positional
folder overrides it. Network shares must be readable by the Windows account
and execution environment running NoTunes. An access failure aborts the scan.

## Commands

```powershell
dotnet run --project Source/NoTunes.csproj -- --help
dotnet run --project Source/NoTunes.csproj -- devices
dotnet run --project Source/NoTunes.csproj -- probe
dotnet run --project Source/NoTunes.csproj -- probe --read-catalog --json
dotnet run --project Source/NoTunes.csproj -- probe --device 00008030-0011223344556677 --timeout 30 --json
dotnet run --project Source/NoTunes.csproj -- scan
dotnet run --project Source/NoTunes.csproj -- scan "D:\Music"
dotnet run --project Source/NoTunes.csproj -- scan "D:\Music" --json
```

After building, the equivalent executable is
`Source\bin\Debug\net10.0\NoTunes.exe`.

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
- `sync [folder] [--dry-run] [--device <UDID>]` is reserved for the upcoming native
  synchronization engine. It currently validates the source and exits with a
  clear unsupported-operation message.

Device commands take `--timeout` (1–300 seconds per request) and `--json`.
With multiple USB devices, `probe` requires `--device`; it never chooses one
arbitrarily. Errors go to stderr. JSON results go to stdout.

## Device prerequisites

Apple device support and Apple Mobile Device Service must be installed and
running on Windows. The application connects to the service at
`127.0.0.1:27015`; it does not bundle Apple's drivers or DLLs. The iTunes UI is
not used. Installation of device support and the initial trust relationship can
be handled by Apple's Windows device software.

Connect the iPhone over USB, unlock it and trust the computer. The prototype
uses the existing pairing record; it does not create or replace pairings.
TLS verifies the device certificate against that record. Pairing certificates
and private keys are never included in diagnostic output or JSON.

The target is iOS 27. The read-only probe, pairing, TLS, AFC, ATC startup and
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
