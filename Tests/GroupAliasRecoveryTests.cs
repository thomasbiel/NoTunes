using NoTunes.Apple;
using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class GroupAliasRecoveryTests
{
    private const long SharedArtistId = 1011;
    private const long NewAlbumId = 2012;
    private const long CanonicalArtistRow = 11;
    private const long AliasArtistRow = 22;
    private const long NewAlbumRow = 13;
    private const int RecoverableJournalVersion = 2;
    private const string NewAlbum = "New Album";

    [TestCase("Boney M.", "Boney M")]
    [TestCase("R.E.M.", "REM")]
    public async Task Recovers_A_Known_Alias_Without_Rewriting_The_Signed_Request(string canonical, string alias)
    {
        var (before, after, addition, pending) = await CreateScenarioAsync(canonical, alias);
        var originalOperation = pending.Operation.ToArray();

        Assert.DoesNotThrow(() => SyncEngine.VerifyResult(
            before,
            after,
            [addition],
            []));

        Assert.That(SyncRecovery.Assess(pending, after, [addition]), Is.Empty);
        Assert.That(pending.Operation, Is.EqualTo(originalOperation));
        Assert.That(addition.Metadata.AlbumArtist, Is.EqualTo(alias));
        Assert.That(addition.Metadata.Item[MediaKeys.SortAlbumArtist], Is.EqualTo(alias));
    }

    [TestCase("row")]
    [TestCase("sort")]
    [TestCase("identity")]
    [TestCase("name")]
    public async Task Rejects_Unrequested_Changes_Despite_A_Known_Alias(string mutation)
    {
        var (before, after, addition, pending) = await CreateScenarioAsync("Boney M.", "Boney M");
        var stored = after.Tracks.Single(track => track.Id == addition.Id);
        var metadata = stored.Metadata!;
        var wrong = stored with
        {
            Metadata = mutation switch
            {
                "row" => metadata with { AlbumArtistRowId = AliasArtistRow },
                "sort" => metadata with { SortAlbumArtist = "Unexpected sort" },
                "identity" => metadata with { AlbumArtistId = SharedArtistId + 1 },
                "name" => metadata with { AlbumArtist = "Different Artist" },
                _ => throw new AssertionException("Unknown mutation.")
            }
        };

        var incorrect = after with { Tracks = [.. before.Tracks, wrong] };

        Assert.Throws<IOException>(() => SyncEngine.VerifyResult(
            before,
            incorrect,
            [addition],
            []));

        Assert.Throws<IOException>(() => SyncRecovery.Assess(pending, incorrect, [addition]));
    }

    [TestCase("Artist", "Other Artist")]
    [TestCase("Boney M.", "Different Band")]
    [TestCase("...", ".")]
    public async Task Requires_Evidence_Of_A_Real_Spelling_Alias(string canonical, string alias)
    {
        var (before, after, addition, pending) = await CreateScenarioAsync(canonical, alias);

        Assert.Throws<IOException>(() => SyncEngine.VerifyResult(
            before,
            after,
            [addition],
            []));

        Assert.Throws<IOException>(() => SyncRecovery.Assess(pending, after, [addition]));
    }

    private static async Task<(
        DeviceCatalogue Before,
        DeviceCatalogue After,
        PreparedTrack Addition,
        PendingSync Pending)> CreateScenarioAsync(string canonical, string alias)
    {
        using var fixture = new CatalogueFixture();
        fixture.Execute($"""
            UPDATE album_artist SET album_artist='{canonical}',sort_album_artist='{canonical}' WHERE album_artist_pid=11;
            INSERT INTO album_artist VALUES(22,1011,'{alias}','{alias}');
            """);

        var before = await CatalogueReader.ReadAsync(fixture.Snapshot(), CancellationToken.None);
        var addition = LegacyAddition(alias);
        var stored = FeatureTestData.Stored(addition);
        stored = stored with
        {
            Metadata = stored.Metadata! with
            {
                AlbumRowId = NewAlbumRow,
                AlbumArtistRowId = CanonicalArtistRow,
                AlbumArtist = canonical,
                SortAlbumArtist = canonical
            }
        };

        var after = before with
        {
            Anchor = before.Anchor + 1,
            Tracks = [.. before.Tracks, stored],
            AlbumGroups = [.. before.AlbumGroups!, new CatalogueGroup(
                NewAlbumRow,
                NewAlbumId,
                NewAlbum,
                NewAlbum,
                CanonicalArtistRow,
                canonical)]
        };

        var pending = new PendingSync(
            RecoverableJournalVersion,
            new LibraryIdentity("id", "binding"),
            CatalogueCheckpoint.Capture(before),
            [JournalTrack.Capture(addition)],
            [],
            SyncOperations.Serialize(before, [addition], []),
            [1, 2]);

        return (before, after, addition, pending);
    }

    private static PreparedTrack LegacyAddition(string artist)
    {
        var track = SyncTestData.Track();
        var item = new Dictionary<string, object>(track.Metadata.Item)
        {
            [MediaKeys.Album] = NewAlbum,
            [MediaKeys.SortAlbum] = NewAlbum,
            [MediaKeys.AlbumArtist] = artist,
            [MediaKeys.SortAlbumArtist] = artist
        };

        return track with
        {
            AlbumId = NewAlbumId,
            ArtistId = SharedArtistId,
            Metadata = track.Metadata with { Album = NewAlbum, AlbumArtist = artist, Item = item }
        };
    }
}
