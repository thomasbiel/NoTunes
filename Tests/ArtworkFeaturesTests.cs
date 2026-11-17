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
        var peer = new AtcPeer(
            SyncTestData.Capabilities(),
            SyncTestData.Allowed(),
            SyncTestData.Ready(),
            SyncTestData.Message(AtcCommands.AssetManifest, SyncTestData.Manifest(SyncTestData.Asset(track.AssetId, parts))),
            SyncTestData.Message(AtcCommands.SyncFinished))
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
            Is.EquivalentTo(new[] { MediaKeys.ArtworkCacheId, MediaKeys.ArtworkChecksum }));
        Assert.That(update.ContainsKey(MediaKeys.Audio), Is.False);
    }

    [Test]
    public async Task Reads_Live_Artwork_Ids_And_Verifies_The_Permanent_Original()
    {
        using var fixture = new CatalogueFixture();
        fixture.Execute("""
            CREATE TABLE artwork(artwork_token TEXT,artwork_source_type INTEGER,artwork_type INTEGER,
              artwork_variant_type INTEGER,relative_path TEXT);
            CREATE TABLE artwork_token(entity_pid INTEGER,artwork_token TEXT,artwork_source_type INTEGER,
              artwork_type INTEGER,entity_type INTEGER,artwork_variant_type INTEGER);
            INSERT INTO artwork VALUES('101',300,1,0,'aa/cover.jpg'),('501',300,6,0,'aa/old.jpg');
            INSERT INTO artwork_token VALUES(1,'101',300,1,0,0),(12,'601',300,6,4,0);
            CREATE TABLE best_artwork_token AS SELECT * FROM artwork_token WHERE entity_pid=1;
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
        var path = Path.Combine(source.Root, "covered.mp3");
        File.Copy(SyncTestData.FixturePath, path);
        File.Copy(SyncTestData.FixturePath, Path.Combine(source.Root, "plain.mp3"));
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Pictures = [new TagLib.Picture
            {
                Data = new TagLib.ByteVector(FeatureTestData.Png()),
                MimeType = "image/png",
                Type = TagLib.PictureType.FrontCover
            }];
            file.Save();
        }

        var original = File.ReadAllBytes(path);
        await using var services = new ServiceCollection().AddNoTunes(TextWriter.Null, TextWriter.Null)
            .BuildServiceProvider();
        var library = await services.GetRequiredService<SourceScanner>().ScanAsync(source.Root, CancellationToken.None);
        var catalogue = SyncPlannerTests.Catalogue([]) with { ArtworkAvailable = true, ArtworkHighWaterMark = 601 };
        var result = await SourcePreparation.PrepareAsync(
            library,
            new SyncPlan("phone", 7, library.Tracks, [], 0),
            catalogue,
            services.GetRequiredService<ISourceFileSystem>(),
            prepared,
            CancellationToken.None,
            artworkOnly: true);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Metadata.Artwork!.CacheId, Is.EqualTo(602));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
        Assert.That(File.ReadAllBytes(result[0].LocalPath), Is.EqualTo(original));
        Assert.That(Directory.GetFiles(prepared.Root), Has.Length.EqualTo(1));
    }
}
