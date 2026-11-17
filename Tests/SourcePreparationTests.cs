using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class SourcePreparationTests
{
    [Test]
    public async Task Skips_Unrecognized_Leading_Data_Without_Changing_The_Source_Or_Keeping_The_Failed_Copy()
    {
        const int leadingDataBytes = 512 * 1024;
        const string invalidName = "!leading-data.mp3";
        const string validName = "valid.mp3";
        using var source = new SyncWorkspace();
        using var output = new SyncWorkspace();
        var validPath = SourceTestData.WriteTrack(
            source.Root,
            validName,
            "Album",
            "Artist");
        var audio = await File.ReadAllBytesAsync(validPath);
        var invalidBytes = new byte[leadingDataBytes + audio.Length];
        audio.CopyTo(invalidBytes, leadingDataBytes);
        var invalidPath = Path.Combine(source.Root, invalidName);
        await File.WriteAllBytesAsync(invalidPath, invalidBytes);
        var skipped = new List<SkippedSourceFile>();

        var prepared = await SourceTestData.PrepareAsync(
            source.Root,
            SyncPlannerTests.Catalogue([]),
            output,
            onSkipped: skipped.Add);

        Assert.That(prepared.Single().Source.RelativePath, Is.EqualTo(validName));
        Assert.That(skipped.Single().RelativePath, Is.EqualTo(invalidName));
        Assert.That(skipped.Single().Reason, Does.Contain("MPEG audio header not found"));
        Assert.That(await File.ReadAllBytesAsync(invalidPath), Is.EqualTo(invalidBytes));
        Assert.That(Directory.GetFiles(output.Root, "*.mp3"), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task Uses_The_Selected_Group_Without_Rewriting_Source_Tags([Values] bool album)
    {
        using var fixture = new CatalogueFixture();
        fixture.Execute(album
            ? "INSERT INTO album VALUES(13,1013,'Album',11,'Other Sort'),(14,1014,'Chosen Album',11,'Chosen Sort');"
            : """
                INSERT INTO album_artist VALUES(22,1022,'Artist','Other Sort'),(23,1023,'Chosen Artist','Chosen Sort');
                INSERT INTO album VALUES(14,1014,'Album',23,'Chosen Album Sort');
                """);

        var catalogue = await CatalogueReader.ReadAsync(fixture.Snapshot(), CancellationToken.None);
        using var source = new SyncWorkspace();
        using var output = new SyncWorkspace();
        SourceTestData.WriteTrack(
            source.Root,
            "one.mp3",
            "Album",
            "Artist");

        File.Copy(Path.Combine(source.Root, "one.mp3"), Path.Combine(source.Root, "two.mp3"));
        var bytes = await File.ReadAllBytesAsync(Path.Combine(source.Root, "one.mp3"));
        var prompts = 0;
        var prepared = await SourceTestData.PrepareAsync(
            source.Root,
            catalogue,
            output,
            (_, _) =>
            {
                prompts++;
                return ValueTask.FromResult<string?>(album ? "Chosen Album" : "Chosen Artist");
            });

        Assert.That(prompts, Is.EqualTo(1));
        Assert.That(prepared.Select(track => track.AlbumId), Is.All.EqualTo(1014));
        Assert.That(prepared.Select(track => track.ArtistId), Is.All.EqualTo(album ? 1011 : 1023));
        var item = prepared[0].Metadata.Item;
        Assert.That(item[MediaKeys.Album], Is.EqualTo(album ? "Chosen Album" : "Album"));
        Assert.That(item[MediaKeys.AlbumArtist], Is.EqualTo(album ? "Artist" : "Chosen Artist"));
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(source.Root, "one.mp3")), Is.EqualTo(bytes));
        Assert.That(await File.ReadAllBytesAsync(prepared[0].LocalPath), Is.EqualTo(bytes));
    }

    [Test]
    public async Task Accepts_A_File_System_Root_As_The_Source()
    {
        using var source = new SyncWorkspace();
        using var output = new SyncWorkspace();
        SourceTestData.WriteTrack(
            source.Root,
            "one.mp3",
            "Album",
            "Artist");

        var scanned = await SourceTestData.ScanAsync(source.Root);
        var root = Path.GetPathRoot(source.Root)!;

        var track = scanned.Tracks[0] with { RelativePath = Path.GetRelativePath(root, Path.Combine(source.Root, "one.mp3")) };
        var library = new SourceLibrary(root, [track]);

        var tracks = await SourceTestData.PrepareAsync(
            library,
            SyncPlannerTests.Catalogue([]),
            output);

        Assert.That(tracks, Has.Count.EqualTo(1));
        Assert.That(
            await File.ReadAllBytesAsync(tracks[0].LocalPath),
            Is.EqualTo(await File.ReadAllBytesAsync(Path.Combine(source.Root, "one.mp3"))));
    }

    [Test]
    public async Task Rejects_A_Track_Outside_The_Source()
    {
        using var source = new SyncWorkspace();
        using var output = new SyncWorkspace();
        SourceTestData.WriteTrack(
            source.Root,
            "one.mp3",
            "Album",
            "Artist");

        var scanned = await SourceTestData.ScanAsync(source.Root);
        var track = scanned.Tracks[0] with { RelativePath = Path.Combine("..", "outside.mp3") };
        await Assert.ThrowsAsync<IOException>(() => SourceTestData.PrepareAsync(
            scanned with { Tracks = [track] },
            SyncPlannerTests.Catalogue([]),
            output));
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
        SourceTestData.WriteTrack(
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

        var tracks = await SourceTestData.PrepareAsync(source.Root, catalogue, output);
        Assert.That(tracks[0].AlbumId, Is.EqualTo(1012));
        Assert.That(tracks[0].ArtistId, Is.EqualTo(1011));
    }

    [Test]
    public async Task Preserves_Stored_Group_Sort_Names_When_New_Tags_Disagree()
    {
        using var source = new SyncWorkspace();
        using var output = new SyncWorkspace();
        using var fixture = new CatalogueFixture();
        SourceTestData.WriteTrack(
            source.Root,
            "one.mp3",
            "ALBUM",
            "ARTIST",
            file =>
            {
                file.Tag.Composers = ["Composer"];
                file.Tag.AlbumSort = "Replacement album sort";
                file.Tag.PerformersSort = ["Replacement artist sort"];
                file.Tag.AlbumArtistsSort = ["Replacement album artist sort"];
                file.Tag.ComposersSort = ["Replacement composer sort"];
            });

        var catalogue = await CatalogueReader.ReadAsync(fixture.Snapshot(), CancellationToken.None);
        var tracks = await SourceTestData.PrepareAsync(source.Root, catalogue, output);
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
        foreach (var (name, artist) in new[] { ("one.mp3", "First Artist"), ("two.mp3", "Second Artist") })
        {
            SourceTestData.WriteTrack(
                source.Root,
                name,
                "Sampler",
                artist,
                file =>
                {
                    ((TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true)).IsCompilation = true;
                    file.Tag.AlbumArtists = ["Various Artists"];
                    file.Tag.AlbumArtistsSort = [];
                });
        }

        var tracks = await SourceTestData.PrepareAsync(source.Root, SyncPlannerTests.Catalogue([]), output);
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
        SourceTestData.WriteTrack(
            source.Root,
            "one.mp3",
            "Album",
            "Artist");

        SourceTestData.WriteTrack(
            source.Root,
            "two.mp3",
            album,
            artist);

        var tracks = await SourceTestData.PrepareAsync(source.Root, SyncPlannerTests.Catalogue([]), output);
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
        var tracks = await SourceTestData.PrepareAsync(
            source.Root,
            SyncPlannerTests.Catalogue([]),
            output);

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
    public async Task Rejects_Changed_Source_Bytes_Even_When_Size_And_Timestamp_Are_Unchanged([Values] bool skipInvalidFiles)
    {
        using var source = new SyncWorkspace();
        using var output = new SyncWorkspace();
        var path = Path.Combine(source.Root, "one.mp3");
        File.Copy(SyncTestData.FixturePath, path);
        var library = await SourceTestData.ScanAsync(source.Root);
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(path, bytes);
        File.SetLastWriteTimeUtc(path, library.Tracks[0].LastWriteTimeUtc);

        var skipped = new List<SkippedSourceFile>();
        await Assert.ThrowsAsync<IOException>(() => SourceTestData.PrepareAsync(
            library,
            SyncPlannerTests.Catalogue([]),
            output,
            onSkipped: skipInvalidFiles ? skipped.Add : null));
        Assert.That(skipped, Is.Empty);
    }

    [Test]
    public void Rejects_A_Fake_Mp3_Before_Any_Phone_Write()
    {
        using var source = new SyncWorkspace();
        var path = Path.Combine(source.Root, "fake.mp3");
        File.WriteAllText(path, "Not an MP3");
        Assert.Throws<InvalidDataException>(() => Mp3Metadata.Read(path, "fake.mp3"));
    }
}
