using Microsoft.Extensions.DependencyInjection;
using NoTunes.Music;
using NoTunes.Platform;
using NUnit.Framework;

namespace NoTunes.Tests;

public sealed class SourcePreparationTests
{
    [Test]
    public async Task Accepts_A_File_System_Root_As_The_Source()
    {
        using var source = new SyncWorkspace();
        using var output = new SyncWorkspace();
        WriteTrack(
            source.Root,
            "one.mp3",
            "Album",
            "Artist");
        await using var services = new ServiceCollection().AddNoTunes(TextWriter.Null, TextWriter.Null).BuildServiceProvider();
        var scanned = await services.GetRequiredService<SourceScanner>().ScanAsync(source.Root, CancellationToken.None);
        var root = Path.GetPathRoot(source.Root)!;
        var library = new SourceLibrary(root, [scanned.Tracks[0] with
        {
            RelativePath = Path.GetRelativePath(root, Path.Combine(source.Root, "one.mp3"))
        }]);
        var tracks = await SourcePreparation.PrepareAsync(
            library,
            new SyncPlan(
                "phone",
                7,
                library.Tracks,
                [],
                0),
            SyncPlannerTests.Catalogue([]),
            services.GetRequiredService<ISourceFileSystem>(),
            output,
            CancellationToken.None);
        Assert.That(tracks, Has.Count.EqualTo(1));
        Assert.That(await File.ReadAllBytesAsync(tracks[0].LocalPath),
            Is.EqualTo(await File.ReadAllBytesAsync(Path.Combine(source.Root, "one.mp3"))));
    }

    [Test]
    public async Task Rejects_A_Track_Outside_The_Source()
    {
        using var source = new SyncWorkspace();
        using var output = new SyncWorkspace();
        WriteTrack(
            source.Root,
            "one.mp3",
            "Album",
            "Artist");
        await using var services = new ServiceCollection().AddNoTunes(TextWriter.Null, TextWriter.Null).BuildServiceProvider();
        var scanned = await services.GetRequiredService<SourceScanner>().ScanAsync(source.Root, CancellationToken.None);
        var track = scanned.Tracks[0] with { RelativePath = Path.Combine("..", "outside.mp3") };
        await Assert.ThrowsAsync<IOException>(() => SourcePreparation.PrepareAsync(
            scanned,
            new SyncPlan(
                "phone",
                7,
                [track],
                [],
                0),
            SyncPlannerTests.Catalogue([]),
            services.GetRequiredService<ISourceFileSystem>(),
            output,
            CancellationToken.None));
    }

    [TestCase("Album", "ALBUM", "Artist", "ARTIST")]
    [TestCase("Björk", "Björk", "Artist", "ARTIST")]
    [TestCase("Album", "ALBUM", "Björk", "Björk")]
    public async Task Reuses_Existing_Groups_Across_Ascii_Case_Variants(
        string existingAlbum,
        string sourceAlbum,
        string existingArtist,
        string sourceArtist)
    {
        using var source = new SyncWorkspace();
        using var output = new SyncWorkspace();
        WriteTrack(
            source.Root,
            "one.mp3",
            sourceAlbum,
            sourceArtist);
        var catalogue = SyncPlannerTests.Catalogue([]) with
        {
            Albums = new Dictionary<string, long> { [CatalogueReader.GroupKey(existingAlbum, existingArtist)] = 1012 },
            Artists = new Dictionary<string, long> { [existingArtist] = 1011 },
            OccupiedIds = new HashSet<long> { 1001, 1002, 1011, 1012 }
        };
        var tracks = await PrepareAsync(source.Root, catalogue, output);
        Assert.That(tracks[0].AlbumId, Is.EqualTo(1012));
        Assert.That(tracks[0].ArtistId, Is.EqualTo(1011));
    }

    [Test]
    public async Task Preserves_Stored_Group_Sort_Names_When_New_Tags_Disagree()
    {
        using var source = new SyncWorkspace();
        using var output = new SyncWorkspace();
        using var fixture = new CatalogueFixture();
        WriteTrack(
            source.Root,
            "one.mp3",
            "ALBUM",
            "ARTIST");
        using (var file = TagLib.File.Create(Path.Combine(source.Root, "one.mp3")))
        {
            file.Tag.Composers = ["Composer"];
            file.Tag.AlbumSort = "Replacement album sort";
            file.Tag.PerformersSort = ["Replacement artist sort"];
            file.Tag.AlbumArtistsSort = ["Replacement album artist sort"];
            file.Tag.ComposersSort = ["Replacement composer sort"];
            file.Save();
        }

        var catalogue = await CatalogueReader.ReadAsync(fixture.Snapshot(), CancellationToken.None);
        var tracks = await PrepareAsync(source.Root, catalogue, output);
        var item = tracks[0].Metadata.Item;
        Assert.That(item[MediaKeys.SortAlbum], Is.EqualTo("Album, The"));
        Assert.That(item[MediaKeys.SortArtist], Is.EqualTo("Performer, The"));
        Assert.That(item[MediaKeys.SortAlbumArtist], Is.EqualTo("Artist, The"));
        Assert.That(item[MediaKeys.SortComposer], Is.EqualTo("Composer, The"));
    }

    [Test]
    public async Task Groups_Compilation_Tracks_Under_Their_Explicit_Album_Artist()
    {
        using var source = new SyncWorkspace();
        using var output = new SyncWorkspace();
        WriteTrack(
            source.Root,
            "one.mp3",
            "Sampler",
            "First Artist");
        WriteTrack(
            source.Root,
            "two.mp3",
            "Sampler",
            "Second Artist");
        foreach (var path in Directory.GetFiles(source.Root))
        {
            using var file = TagLib.File.Create(path);
            ((TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true)).IsCompilation = true;
            file.Tag.AlbumArtists = ["Various Artists"];
            file.Tag.AlbumArtistsSort = [];
            file.Save();
        }

        var tracks = await PrepareAsync(source.Root, SyncPlannerTests.Catalogue([]), output);
        Assert.That(tracks.Select(track => track.AlbumId).Distinct().Count(), Is.EqualTo(1));
        Assert.That(tracks.Select(track => track.ArtistId).Distinct().Count(), Is.EqualTo(1));
        Assert.That(tracks.All(track => (bool)track.Metadata.Item[MediaKeys.IsCompilation]), Is.True);
    }

    [TestCase("Album", "Artist", true)]
    [TestCase("ALBUM", "ARTIST", true)]
    [TestCase("Album", "Björk", false)]
    public async Task Reuses_New_Groups_Only_For_Qualified_Name_Matches(string album, string artist, bool matches)
    {
        using var source = new SyncWorkspace();
        using var output = new SyncWorkspace();
        WriteTrack(
            source.Root,
            "one.mp3",
            "Album",
            "Artist");
        WriteTrack(
            source.Root,
            "two.mp3",
            album,
            artist);
        var tracks = await PrepareAsync(source.Root, SyncPlannerTests.Catalogue([]), output);
        Assert.That(tracks.Select(track => track.AlbumId).Distinct().Count(), Is.EqualTo(matches ? 1 : 2));
        Assert.That(tracks.Select(track => track.ArtistId).Distinct().Count(), Is.EqualTo(matches ? 1 : 2));
    }

    [Test]
    public async Task Prepares_Verified_Local_Copies_And_Reuses_Album_And_Artist_Identities()
    {
        using var source = new SyncWorkspace();
        using var output = new SyncWorkspace();
        var album = Directory.CreateDirectory(Path.Combine(source.Root, "album"));
        File.Copy(SyncTestData.FixturePath, Path.Combine(album.FullName, "one.mp3"));
        File.Copy(SyncTestData.FixturePath, Path.Combine(album.FullName, "two.mp3"));
        await using var services = new ServiceCollection().AddNoTunes(TextWriter.Null, TextWriter.Null).BuildServiceProvider();
        var scanner = services.GetRequiredService<SourceScanner>();
        var library = await scanner.ScanAsync(source.Root, CancellationToken.None);
        var plan = new SyncPlan(
            "phone",
            7,
            library.Tracks,
            [],
            0);
        var tracks = await SourcePreparation.PrepareAsync(
            library,
            plan,
            SyncPlannerTests.Catalogue([]),
            services.GetRequiredService<ISourceFileSystem>(),
            output,
            CancellationToken.None);
        Assert.That(tracks, Has.Count.EqualTo(2));
        Assert.That(tracks.Select(track => track.Id).Distinct().Count(), Is.EqualTo(2));
        Assert.That(tracks.Select(track => track.AlbumId).Distinct().Count(), Is.EqualTo(1));
        Assert.That(tracks.Select(track => track.ArtistId).Distinct().Count(), Is.EqualTo(1));
        foreach (var track in tracks)
        {
            Assert.That(await File.ReadAllBytesAsync(track.LocalPath), Is.EqualTo(await File.ReadAllBytesAsync(SyncTestData.FixturePath)));
        }

        Assert.That(tracks[0].Metadata.Item[MediaKeys.Title], Is.EqualTo("Format title"));
    }

    [Test]
    public async Task Rejects_Changed_Source_Bytes_Even_When_Size_And_Timestamp_Are_Unchanged()
    {
        using var source = new SyncWorkspace();
        using var output = new SyncWorkspace();
        var path = Path.Combine(source.Root, "one.mp3");
        File.Copy(SyncTestData.FixturePath, path);
        await using var services = new ServiceCollection().AddNoTunes(TextWriter.Null, TextWriter.Null).BuildServiceProvider();
        var library = await services.GetRequiredService<SourceScanner>().ScanAsync(source.Root, CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(path, bytes);
        File.SetLastWriteTimeUtc(path, library.Tracks[0].LastWriteTimeUtc);
        await Assert.ThrowsAsync<IOException>(() => SourcePreparation.PrepareAsync(
            library,
            new SyncPlan(
                "phone",
                7,
                library.Tracks,
                [],
                0),
            SyncPlannerTests.Catalogue([]),
            services.GetRequiredService<ISourceFileSystem>(),
            output,
            CancellationToken.None));
    }

    [Test]
    public void Rejects_A_Fake_Mp3_Before_Any_Phone_Write()
    {
        using var source = new SyncWorkspace();
        var path = Path.Combine(source.Root, "fake.mp3");
        File.WriteAllText(path, "Not an MP3");
        Assert.Throws<InvalidDataException>(() => Mp3Metadata.Read(path, "fake.mp3"));
    }

    private static void WriteTrack(string root, string name, string album, string artist)
    {
        var path = Path.Combine(root, name);
        File.Copy(SyncTestData.FixturePath, path);
        using var file = TagLib.File.Create(path);
        file.Tag.Album = album;
        file.Tag.Performers = [artist];
        file.Tag.AlbumArtists = [artist];
        file.Save();
    }

    private static async Task<IReadOnlyList<PreparedTrack>> PrepareAsync(
        string root,
        DeviceCatalogue catalogue,
        SyncWorkspace output)
    {
        await using var services = new ServiceCollection().AddNoTunes(TextWriter.Null, TextWriter.Null).BuildServiceProvider();
        var library = await services.GetRequiredService<SourceScanner>().ScanAsync(root, CancellationToken.None);
        return await SourcePreparation.PrepareAsync(
            library,
            new SyncPlan(
                "phone",
                catalogue.Anchor,
                library.Tracks,
                [],
                0),
            catalogue,
            services.GetRequiredService<ISourceFileSystem>(),
            output,
            CancellationToken.None);
    }
}
