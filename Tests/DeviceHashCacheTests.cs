using System.Security.Cryptography;
using System.Text.Json;
using NoTunes.Apple;
using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class DeviceHashCacheTests
{
    private const string Device = "00008030-00112CE23A86402E";
    private static readonly LibraryIdentity Identity = new("library", "binding");
    private static readonly DeviceTrack Track = new(
        101,
        "Song",
        "music/song.mp3",
        3);

    private SyncWorkspace workspace = null!;
    private AfcPeer peer = null!;
    private AfcClient afc = null!;
    private StringWriter error = null!;
    private DeviceCatalogue catalogue = null!;

    [SetUp]
    public void SetUp()
    {
        this.workspace = new SyncWorkspace();
        this.error = new StringWriter();
        this.peer = new AfcPeer { AllowPipelinedFileInfo = true };
        this.peer.Files[Track.Path!] = "abc"u8.ToArray();
        this.afc = new AfcClient(this.peer, TimeSpan.FromSeconds(2));
        this.catalogue = CatalogueTestData.Create([Track]);
    }

    [TearDown]
    public async Task TearDown()
    {
        await this.afc.DisposeAsync();
        this.error.Dispose();
        this.workspace.Dispose();
    }

    [Test]
    public async Task Reuses_Verified_Hashes_Without_Reading_Audio([Values] bool prefetch)
    {
        await this.PrimeAsync();
        var reads = this.peer.Reads;
        var queries = this.peer.Operations.Count(operation => operation == AfcOperation.GetFileInfo);
        var cache = this.Open();
        if (prefetch)
        {
            await cache.PrefetchAsync(
                this.afc,
                new SourceLibrary("source", [new(
                    "song.mp3",
                    Track.Size,
                    DateTime.UnixEpoch,
                    "unused")]),
                this.catalogue,
                CancellationToken.None);
        }

        var hash = await cache.ReadAsync(this.afc, Track, CancellationToken.None);
        Assert.That(hash, Is.EqualTo(Convert.ToHexString(SHA1.HashData("abc"u8))));
        Assert.That(cache.Hits, Is.EqualTo(1));
        Assert.That(cache.Checks, Is.Zero);
        Assert.That(this.peer.Reads, Is.EqualTo(reads));
        Assert.That(this.peer.Operations.Count(operation => operation == AfcOperation.GetFileInfo), Is.EqualTo(queries + 1));
    }

    [Test]
    public async Task Hashes_A_Miss_On_The_Device_With_Exactly_Two_Attribute_Queries()
    {
        await this.PrimeAsync();
        Assert.That(this.peer.Operations.Count(operation => operation == AfcOperation.GetFileInfo), Is.EqualTo(2));
        Assert.That(this.peer.HashPaths, Is.EqualTo([Track.Path]));
        Assert.That(this.peer.ReadPaths, Is.Empty);
    }

    [Test]
    public async Task Full_Readback_Detects_Changed_Bytes_With_Unchanged_Attributes_And_Refreshes_The_Cache()
    {
        var original = await this.PrimeAsync();
        this.peer.Files[Track.Path!] = "xyz"u8.ToArray();
        var cached = this.Open();
        Assert.That(await cached.ReadAsync(this.afc, Track, CancellationToken.None), Is.EqualTo(original));
        var full = this.Open(fullReadback: true);
        var actual = await this.ReadAndSaveAsync(full);
        Assert.That(actual, Is.EqualTo(Convert.ToHexString(SHA1.HashData("xyz"u8))));
        Assert.That(full.Hits, Is.Zero);
        Assert.That(full.Checks, Is.EqualTo(1));
        Assert.That(await this.Open().ReadAsync(this.afc, Track, CancellationToken.None), Is.EqualTo(actual));
    }

    [TestCase(AfcKeys.ModificationTime)]
    [TestCase("st_birthtime")]
    public async Task Rehashes_When_Stable_Attributes_Change(string key)
    {
        await this.PrimeAsync();
        this.peer.FileInfoOverrides[Track.Path!] = new() { [key] = "2" };
        var cache = this.Open();
        await this.AssertCacheMissAsync(cache);
    }

    [Test]
    public async Task Ignores_Access_Time_Changes()
    {
        this.peer.FileInfoOverrides[Track.Path!] = new() { [AfcKeys.AccessTime] = "1" };
        await this.PrimeAsync();
        this.peer.FileInfoOverrides[Track.Path!][AfcKeys.AccessTime] = "2";
        var cache = this.Open();
        await cache.ReadAsync(this.afc, Track, CancellationToken.None);
        Assert.That(cache.Hits, Is.EqualTo(1));
    }

    [TestCase("device")]
    [TestCase("library")]
    [TestCase("binding")]
    [TestCase("database")]
    [TestCase("container")]
    public async Task Separates_Devices_And_Library_Identities(string changed)
    {
        await this.PrimeAsync();
        var cache = DeviceHashCache.Open(
            this.workspace.Root,
            changed == "device" ? "another-phone" : Device,
            changed == "library" ? Identity with { LibraryId = "another" }
                : changed == "binding" ? Identity with { Binding = "another" } : Identity,
            changed == "database" ? this.catalogue with { DatabaseId = this.catalogue.DatabaseId + 1 }
                : changed == "container" ? this.catalogue with { ContainerId = this.catalogue.ContainerId + 1 } : this.catalogue,
            false,
            this.error);

        await this.AssertCacheMissAsync(cache);
    }

    [TestCase("id")]
    [TestCase("path")]
    [TestCase("size")]
    public async Task Rehashes_A_Changed_Track_Identity_Path_Or_Size(string changed)
    {
        await this.PrimeAsync();
        var track = changed switch
        {
            "id" => Track with { Id = Track.Id + 1 },
            "path" => Track with { Path = "music/renamed.mp3" },
            _ => Track with { Size = Track.Size + 1 }
        };

        this.peer.Files[track.Path!] = changed == "size" ? "abcd"u8.ToArray() : "abc"u8.ToArray();
        var cache = this.Open();
        await this.AssertCacheMissAsync(cache, track);
    }

    [TestCase("")]
    [TestCase("invalid")]
    [TestCase("-1")]
    public async Task Does_Not_Cache_Files_Without_A_Usable_Modification_Time(string modified)
    {
        this.peer.FileInfoOverrides[Track.Path!] = new() { [AfcKeys.ModificationTime] = modified };
        await this.PrimeAsync();
        var cache = this.Open();
        await this.AssertCacheMissAsync(cache);
    }

    [TestCase("not json")]
    [TestCase("null")]
    [TestCase("{}")]
    [TestCase("{\"Version\":999}")]
    public async Task Rebuilds_An_Unreadable_Or_Unsupported_Cache(string content)
    {
        await this.PrimeAsync();
        await File.WriteAllTextAsync(this.Open().FilePath!, content);
        var cache = this.Open();
        await cache.ReadAsync(this.afc, Track, CancellationToken.None);
        cache.Save();
        Assert.That(cache.Checks, Is.EqualTo(1));
        Assert.That(this.error.ToString(), Does.Contain("Device hash cache could not be read"));
        Assert.That(Directory.GetFiles(this.workspace.Root, "*.tmp"), Is.Empty);
    }

    [TestCase("hash")]
    [TestCase("sha256")]
    [TestCase("duplicate")]
    [TestCase("attributes")]
    public async Task Rejects_Invalid_Cache_Entries(string changed)
    {
        await this.PrimeAsync();
        var path = this.Open().FilePath!;
        var snapshot = JsonSerializer.Deserialize<DeviceHashSnapshot>(await File.ReadAllTextAsync(path))!;
        var track = snapshot.Tracks.Single();
        var invalid = changed switch
        {
            "hash" => snapshot with { Tracks = [track with { Hash = "invalid" }] },
            "sha256" => snapshot with { Tracks = [track with { Hash = new string('A', 64) }] },
            "duplicate" => snapshot with { Tracks = [track, track] },
            _ => snapshot with { Tracks = [track with { Attributes = null! }] }
        };

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(invalid));
        var cache = this.Open();
        await this.AssertCacheMissAsync(cache);
    }

    [Test]
    public async Task Cache_Write_Failures_Do_Not_Invalidate_A_Verified_Hash()
    {
        var blocked = Path.Combine(this.workspace.Root, "blocked");
        await File.WriteAllTextAsync(blocked, "Not a directory");
        var cache = DeviceHashCache.Open(
            blocked,
            Device,
            Identity,
            this.catalogue,
            false,
            this.error);

        var hash = await this.ReadAndSaveAsync(cache);
        Assert.That(hash, Is.EqualTo(Convert.ToHexString(SHA1.HashData("abc"u8))));
        Assert.That(this.error.ToString(), Does.Contain("Device hash cache could not be saved"));
    }

    [Test]
    public async Task Incomplete_Readback_Does_Not_Create_A_Cache_Entry()
    {
        await using var broken = new AfcPeer
        {
            EarlyEof = true,
            Files = this.peer.Files,
            FileHashError = AfcStatusCode.OperationNotSupported
        };

        await using var afc = new AfcClient(broken, TimeSpan.FromSeconds(2));
        var cache = this.Open();
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.ReadAsync(afc, Track, CancellationToken.None));
        cache.Save();
        var next = this.Open();
        await this.AssertCacheMissAsync(next);
    }

    [Test]
    public async Task Rejects_An_Oversized_Cache_Without_Deserializing_It()
    {
        const long oversizedCacheBytes = 65L * 1024 * 1024;
        var path = this.Open().FilePath!;
        await using (var file = File.Create(path))
        {
            file.SetLength(oversizedCacheBytes);
        }

        var cache = this.Open();
        await cache.ReadAsync(this.afc, Track, CancellationToken.None);
        Assert.That(cache.Checks, Is.EqualTo(1));
        Assert.That(this.error.ToString(), Does.Contain("exceeds its size limit"));
    }

    [Test]
    public async Task Missing_Audio_Is_Not_Hidden_By_A_Cache_Hit()
    {
        await this.PrimeAsync();
        this.peer.Files.Remove(Track.Path!);
        var cache = this.Open();
        Assert.That(await cache.ReadAsync(this.afc, Track, CancellationToken.None), Is.Null);
        Assert.That(cache.Hits, Is.Zero);
    }

    [Test]
    public async Task Changed_File_Type_Or_Wrong_Size_Still_Stops_The_Comparison([Values] bool directory)
    {
        await this.PrimeAsync();
        if (directory)
        {
            this.peer.Directories.Add(Track.Path!);
        }
        else
        {
            this.peer.FileInfoOverrides[Track.Path!] = new() { [AfcKeys.FileSize] = "4" };
        }

        await Assert.ThrowsAsync<IOException>(() => this.Open().ReadAsync(this.afc, Track, CancellationToken.None));
    }

    [Test]
    public async Task Changed_Attributes_During_Hashing_Do_Not_Create_A_Cache_Entry([Values] bool fullReadback)
    {
        void ChangeAttributes() =>
            this.peer.FileInfoOverrides[Track.Path!] = new() { [AfcKeys.ModificationTime] = "2" };

        if (fullReadback)
        {
            this.peer.OnClose = ChangeAttributes;
        }
        else
        {
            this.peer.OnFileHashed = _ => ChangeAttributes();
        }

        var cache = this.Open(fullReadback);
        await Assert.ThrowsAsync<IOException>(() => cache.ReadAsync(this.afc, Track, CancellationToken.None));
        Assert.That(cache.Checks, Is.Zero);
        Assert.That(File.Exists(cache.FilePath), Is.False);
    }

    private async Task AssertCacheMissAsync(DeviceHashCache cache, DeviceTrack? track = null)
    {
        await cache.ReadAsync(this.afc, track ?? Track, CancellationToken.None);
        Assert.That(cache.Hits, Is.Zero);
        Assert.That(cache.Checks, Is.EqualTo(1));
    }

    private DeviceHashCache Open(bool fullReadback = false)
    {
        return DeviceHashCache.Open(
            this.workspace.Root,
            Device,
            Identity,
            this.catalogue,
            fullReadback,
            this.error);
    }

    private async Task<string?> ReadAndSaveAsync(DeviceHashCache cache)
    {
        var hash = await cache.ReadAsync(this.afc, Track, CancellationToken.None);
        cache.Save();
        return hash;
    }

    private async Task<string?> PrimeAsync()
    {
        var cache = this.Open();
        var hash = await this.ReadAndSaveAsync(cache);
        return hash;
    }
}
