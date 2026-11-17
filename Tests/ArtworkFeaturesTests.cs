using NoTunes.Apple;
using NoTunes.Music;
using NUnit.Framework;
using Microsoft.Extensions.DependencyInjection;
using NoTunes.Platform;

namespace NoTunes.Tests;

[TestFixture]
public sealed class ArtworkFeaturesTests
{
    [Test]
    public async Task Repairs_Covers_Without_Resolving_Ambiguous_Group_Identities_Or_Sort_Names([Values] bool ambiguousIds)
    {
        const string album = "Album";
        const string artist = "Artist";
        using var source = new SyncWorkspace();
        using var output = new SyncWorkspace();
        SourceTestData.WriteTrack(
            source.Root,
            "covered.mp3",
            album,
            artist,
            file => file.Tag.Pictures = [FeatureTestData.FrontCover()]);

        await using var services = new ServiceCollection().AddNoTunes(TextWriter.Null, TextWriter.Null)
            .BuildServiceProvider();
        var library = await services.GetRequiredService<SourceScanner>().ScanAsync(source.Root, CancellationToken.None);
        var groupKey = CatalogueReader.GroupKey(album, artist);
        var catalogue = SyncPlannerTests.Catalogue([]) with
        {
            ArtworkAvailable = true,
            Albums = new Dictionary<string, long> { [groupKey] = ambiguousIds ? 0 : 1012 },
            Artists = new Dictionary<string, long> { [artist] = ambiguousIds ? 0 : 1011 },
            SortNames = GroupSortNames.Empty with
            {
                Albums = new Dictionary<string, string?> { [groupKey] = ambiguousIds ? album : null },
                AlbumArtists = new Dictionary<string, string?> { [artist] = ambiguousIds ? artist : null }
            }
        };
        var plan = new SyncPlan(
            "phone",
            catalogue.Anchor,
            library.Tracks,
            [],
            0);
        var fileSystem = services.GetRequiredService<ISourceFileSystem>();
        await Assert.ThrowsAsync<InvalidDataException>(() => SourcePreparation.PrepareAsync(
            library,
            plan,
            catalogue,
            fileSystem,
            output,
            CancellationToken.None));

        var prepared = await SourcePreparation.PrepareAsync(
            library,
            plan,
            catalogue,
            fileSystem,
            output,
            CancellationToken.None,
            artworkOnly: true);
        Assert.That(prepared, Has.Count.EqualTo(1));
        var repair = prepared[0] with { Id = SyncTestData.TrackId, IsArtworkOnly = true };
        var document = Plist.Parse(SyncOperations.Serialize(catalogue, [repair], [])).AsDict();
        var operation = ((object[])document[MediaKeys.Operations])[1].AsDict();
        Assert.That(operation.Text(MediaKeys.Operation), Is.EqualTo(MediaOperations.UpdateTrack));
        Assert.That(operation.Dictionary(MediaKeys.Item).Keys,
            Is.EquivalentTo([MediaKeys.ArtworkCacheId, MediaKeys.ArtworkChecksum]));
        Assert.That(repair.Metadata.Artwork, Is.Not.Null);
    }

    [Test]
    public void Converts_Transparent_Png_To_Jpeg_And_Preserves_An_Existing_Jpeg()
    {
        var image = ArtworkImage.Read(FeatureTestData.Png());
        Assert.That(image.Bytes.Take(2), Is.EqualTo(new byte[] { 255, 216 }));
        Assert.That(image.Checksum, Is.EqualTo(image.Bytes.Length + 1));
        Assert.That(ArtworkImage.Read(image.Bytes).Bytes, Is.EqualTo(image.Bytes));
        Assert.Throws<InvalidDataException>(() => ArtworkImage.Read([1, 2, 3, 4]));
    }

    [TestCase(false, 3L)]
    [TestCase(false, 7L)]
    [TestCase(true, 2L)]
    [TestCase(true, 6L)]
    public async Task Transfers_Artwork_Before_Completing_The_Requested_Asset(bool artworkOnly, long parts)
    {
        var track = FeatureTestData.Covered(artworkOnly);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var files = new AfcPeer { AllowWrites = true };
        var peer = new AtcPeer(SyncTestData.SessionMessages(
            SyncTestData.Message(AtcCommands.AssetManifest, SyncTestData.Manifest(SyncTestData.Asset(track.AssetId, parts))),
            SyncTestData.Message(AtcCommands.SyncFinished)))
        {
            BeforeFrameAsync = (frame, token) => frame >= 4 ? complete.Task.WaitAsync(token) : Task.CompletedTask,
            OnMessageSent = message =>
            {
                if (message.Text(AtcKeys.Command) != AtcCommands.FileComplete) return;

                Assert.That(files.Files[SyncPaths.ArtworkAirlock + track.AssetId], Is.EqualTo(track.Metadata.Artwork!.Bytes));
                Assert.That(message.Dictionary(AtcKeys.Parameters).Text(AtcKeys.AssetPath),
                    Is.EqualTo(artworkOnly ? string.Empty : track.DevicePath));
                complete.TrySetResult();
            }
        };
        await using var afc = new AfcClient(files, TimeSpan.FromSeconds(1));
        await using var atc = new AtcClient(peer, TimeSpan.FromSeconds(1));
        await atc.ReadSyncStatusAsync(CancellationToken.None);
        var session = new AtcSyncSession(atc, TextWriter.Null);
        using var signer = await session.BeginAsync(new LibraryIdentity("id", "binding"), 7, CancellationToken.None);
        await session.FinishMetadataAsync(7, CancellationToken.None);
        await session.TransferAsync(afc, [track], new HashSet<long>(), CancellationToken.None);
        Assert.That(files.Files.ContainsKey(SyncPaths.Airlock + track.AssetId), Is.EqualTo(!artworkOnly));
    }

    [Test]
    public void Artwork_Only_Operations_Do_Not_Resend_Audio_Or_Track_Metadata()
    {
        var catalogue = SyncPlannerTests.Catalogue([]);
        var track = FeatureTestData.Covered(artworkOnly: true);
        var operation = Plist.Parse(SyncOperations.Serialize(catalogue, [track], [])).AsDict();
        var update = ((object[])operation[MediaKeys.Operations])[1].AsDict();
        Assert.That(update.Text(MediaKeys.Operation), Is.EqualTo("update_track"));
        Assert.That(update.Dictionary(MediaKeys.Item).Keys,
            Is.EquivalentTo([MediaKeys.ArtworkCacheId, MediaKeys.ArtworkChecksum]));
        Assert.That(update.ContainsKey(MediaKeys.Audio), Is.False);
    }

    [Test]
    public async Task Reads_Live_Artwork_Ids_And_Verifies_The_Permanent_Original()
    {
        using var fixture = new CatalogueFixture();
        fixture.AddArtworkSchema();
        fixture.Execute("""
            INSERT INTO artwork(artwork_token,artwork_source_type,artwork_type,artwork_variant_type,relative_path)
                VALUES('101',300,1,0,'aa/cover.jpg'),('501',300,6,0,'aa/old.jpg');
            INSERT INTO artwork_token(entity_pid,artwork_token,artwork_source_type,artwork_type,entity_type,
                artwork_variant_type) VALUES(1,'101',300,1,0,0),(12,'601',300,6,4,0);
            INSERT INTO best_artwork_token(entity_pid,entity_type,artwork_type,available_artwork_token,
                artwork_variant_type)
                SELECT entity_pid,entity_type,artwork_type,artwork_token,artwork_variant_type
                FROM artwork_token WHERE entity_pid=1;
            """);
        var catalogue = await CatalogueReader.ReadAsync(fixture.Snapshot(), CancellationToken.None);
        Assert.That(catalogue.ArtworkAvailable, Is.True);
        Assert.That(catalogue.ArtworkHighWaterMark, Is.EqualTo(601));
        var image = ArtworkImage.Read(FeatureTestData.Png()) with { CacheId = 101 };
        var track = FeatureTestData.Covered() with { Id = 101 };
        var files = new AfcPeer();
        files.Files[SyncPaths.ArtworkOriginals + "aa/cover.jpg"] = image.Bytes;
        await using var afc = new AfcClient(files, TimeSpan.FromSeconds(1));
        await SyncEngine.VerifyArtworkAsync(afc, catalogue, track, CancellationToken.None);
        files.Files[SyncPaths.ArtworkOriginals + "aa/cover.jpg"][^1] ^= 1;
        await Assert.ThrowsAsync<IOException>(() => SyncEngine.VerifyArtworkAsync(
            afc,
            catalogue,
            track,
            CancellationToken.None));
        fixture.Execute("DELETE FROM best_artwork_token;");
        var unregistered = await CatalogueReader.ReadAsync(fixture.Snapshot(), CancellationToken.None);
        Assert.That(unregistered.TrackArtwork[101].Path, Is.Null);
    }

    [Test]
    public async Task Prepares_Missing_Covers_Without_Rewriting_The_Source_Or_Copying_Tracks_Without_Covers()
    {
        using var source = new SyncWorkspace();
        using var prepared = new SyncWorkspace();
        var path = SourceTestData.WriteTrack(source.Root, "covered.mp3",
            file => file.Tag.Pictures = [FeatureTestData.FrontCover()]);
        File.Copy(SyncTestData.FixturePath, Path.Combine(source.Root, "plain.mp3"));

        var original = await File.ReadAllBytesAsync(path);
        File.Copy(path, Path.Combine(source.Root, "second-covered.mp3"));
        var catalogue = SyncPlannerTests.Catalogue([]) with { ArtworkAvailable = true, ArtworkHighWaterMark = 601 };
        var result = await SourceTestData.PrepareAsync(
            source.Root,
            catalogue,
            prepared,
            artworkOnly: true);
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result[0].Metadata.Artwork!.CacheId, Is.EqualTo(602));
        Assert.That(result[1].Metadata.Artwork!.CacheId, Is.EqualTo(603));
        Assert.That(result[0].Metadata.Artwork!.LocalPath, Is.Not.Null);
        Assert.That(result[1].Metadata.Artwork!.LocalPath, Is.EqualTo(result[0].Metadata.Artwork!.LocalPath));
        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(original));
        Assert.That(await File.ReadAllBytesAsync(result[0].LocalPath), Is.EqualTo(original));
        Assert.That(Directory.GetFiles(prepared.Root, "*.mp3"), Has.Length.EqualTo(2));
        Assert.That(Directory.GetFiles(prepared.Root, "*.jpg"), Has.Length.EqualTo(1));
    }
}
