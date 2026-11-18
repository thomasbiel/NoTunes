using NoTunes.Apple;
using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class TrackMetadataVerificationTests
{
    [TestCase(4886368L, 110802L)]
    [TestCase(9242610L, 209583L)]
    [TestCase(15112761L, 342693L)]
    [TestCase(44100L, 999L)]
    public void Accepts_The_Submitted_Millisecond_Value_At_A_Sample_Rounding_Boundary(long samples, long milliseconds)
    {
        var addition = Addition();
        addition.Metadata.Audio[MediaKeys.Duration] = samples;
        addition.Metadata.Item[MediaKeys.TotalMilliseconds] = milliseconds;
        var stored = FeatureTestData.Stored(addition).Metadata! with { TotalMilliseconds = milliseconds };

        Assert.That(stored.Matches(addition), Is.True);
    }

    [TestCase(1000L, 1001L)]
    [TestCase(5000L, 5000L)]
    public void Rejects_Unrequested_Or_Inconsistent_Millisecond_Values(long submitted, long stored)
    {
        var addition = Addition();
        addition.Metadata.Item[MediaKeys.TotalMilliseconds] = submitted;
        var metadata = FeatureTestData.Stored(addition).Metadata! with { TotalMilliseconds = stored };

        Assert.That(metadata.Matches(addition), Is.False);
    }

    private const string OtherGroupsSql = """
        INSERT INTO album VALUES(13,1013,'Other album',11,'Other album');
        INSERT INTO album_artist VALUES(22,1022,'Other artist','Other artist');
        INSERT INTO item_artist VALUES('Other performer','Other performer',23);
        INSERT INTO composer VALUES(1024,'Other composer','Other composer',24);
        INSERT INTO genre VALUES(25,'Other genre');
        """;

    private const string InsertAdditionSql = """
        INSERT INTO base_location VALUES(2,'iTunes_Control/Music/F06');
        INSERT INTO item(item_pid,media_type,remote_location_id,base_location_id,track_number,disc_number,is_compilation)
        VALUES(6,8,0,2,3,2,1);
        INSERT INTO item_store VALUES(6,106,0);
        INSERT INTO item_extra(item_pid,title,location,file_size,sort_title,comment,track_count,disc_count,year,
                               total_time_ms,media_kind)
        VALUES(6,'Added','000000000000006A.mp3',5,'Added, The','Comment',9,3,2020,1000,1);
        INSERT INTO item_playback VALUES(6,301,128,44100,44100);
        INSERT INTO item_stats VALUES(6,0);
        UPDATE _MLDatabaseProperties SET value='8';
        """;

    [TestCase("UPDATE item SET album_pid=13 WHERE item_pid=1;")]
    [TestCase("UPDATE item SET album_artist_pid=22 WHERE item_pid=1;")]
    [TestCase("UPDATE item SET item_artist_pid=23 WHERE item_pid=1;")]
    [TestCase("UPDATE item SET composer_pid=24 WHERE item_pid=1;")]
    [TestCase("UPDATE item SET genre_id=25 WHERE item_pid=1;")]
    [TestCase("UPDATE item SET track_number=99 WHERE item_pid=1;")]
    [TestCase("UPDATE item SET disc_number=9 WHERE item_pid=1;")]
    [TestCase("UPDATE item SET is_compilation=1 WHERE item_pid=1;")]
    [TestCase("UPDATE item_extra SET track_count=99 WHERE item_pid=1;")]
    [TestCase("UPDATE item_extra SET disc_count=9 WHERE item_pid=1;")]
    [TestCase("UPDATE item_extra SET year=2000 WHERE item_pid=1;")]
    [TestCase("UPDATE item_extra SET comment='Changed' WHERE item_pid=1;")]
    [TestCase("UPDATE item_extra SET sort_title='Changed' WHERE item_pid=1;")]
    [TestCase("UPDATE item_extra SET total_time_ms=999 WHERE item_pid=1;")]
    [TestCase("UPDATE item_extra SET media_kind=2 WHERE item_pid=1;")]
    [TestCase("UPDATE item_playback SET audio_format=302 WHERE item_pid=1;")]
    [TestCase("UPDATE item_playback SET bit_rate=256 WHERE item_pid=1;")]
    [TestCase("UPDATE item_playback SET sample_rate=48000 WHERE item_pid=1;")]
    [TestCase("UPDATE item_playback SET duration=999 WHERE item_pid=1;")]
    [TestCase("UPDATE item_stats SET remember_bookmark=1 WHERE item_pid=1;")]
    public async Task Rejects_Unrequested_Track_Metadata_Changes_Before_And_After_A_Batch(string mutation)
    {
        using var fixture = new CatalogueFixture();
        fixture.Execute(OtherGroupsSql);
        var before = await fixture.ReadAsync();
        fixture.Execute(mutation);
        var after = await fixture.ReadAsync();
        CatalogueAssertions.RejectsUnrequestedChanges(before, after);
    }

    [TestCase("UPDATE item SET album_pid=13 WHERE item_pid=6;")]
    [TestCase("UPDATE item SET album_artist_pid=22 WHERE item_pid=6;")]
    [TestCase("UPDATE item SET item_artist_pid=23 WHERE item_pid=6;")]
    [TestCase("UPDATE item SET composer_pid=24 WHERE item_pid=6;")]
    [TestCase("UPDATE item SET genre_id=25 WHERE item_pid=6;")]
    [TestCase("UPDATE item SET track_number=99 WHERE item_pid=6;")]
    [TestCase("UPDATE item SET disc_number=9 WHERE item_pid=6;")]
    [TestCase("UPDATE item SET is_compilation=0 WHERE item_pid=6;")]
    [TestCase("UPDATE item_extra SET track_count=99 WHERE item_pid=6;")]
    [TestCase("UPDATE item_extra SET disc_count=9 WHERE item_pid=6;")]
    [TestCase("UPDATE item_extra SET year=2000 WHERE item_pid=6;")]
    [TestCase("UPDATE item_extra SET comment='Changed' WHERE item_pid=6;")]
    [TestCase("UPDATE item_extra SET sort_title='Changed' WHERE item_pid=6;")]
    [TestCase("UPDATE item_extra SET total_time_ms=999 WHERE item_pid=6;")]
    [TestCase("UPDATE item_extra SET media_kind=2 WHERE item_pid=6;")]
    [TestCase("UPDATE item_playback SET audio_format=302 WHERE item_pid=6;")]
    [TestCase("UPDATE item_playback SET bit_rate=256 WHERE item_pid=6;")]
    [TestCase("UPDATE item_playback SET sample_rate=48000 WHERE item_pid=6;")]
    [TestCase("UPDATE item_playback SET duration=999 WHERE item_pid=6;")]
    [TestCase("UPDATE item_stats SET remember_bookmark=1 WHERE item_pid=6;")]
    public async Task Rejects_Incorrect_Metadata_For_An_Addition_Before_Confirming_The_Batch(string mutation)
    {
        using var fixture = new CatalogueFixture();
        fixture.Execute(OtherGroupsSql);
        var before = await fixture.ReadAsync();
        fixture.Execute(InsertAdditionSql);
        var correct = await fixture.ReadAsync();
        var addition = Addition();
        Assert.DoesNotThrow(() => SyncEngine.VerifyResult(
            before,
            correct,
            [addition],
            []));

        fixture.Execute(mutation);
        var after = await fixture.ReadAsync();
        Assert.Throws<IOException>(() => SyncEngine.VerifyResult(
            before,
            after,
            [addition],
            []));
    }

    [Test]
    public async Task Accepts_Receiver_Defaults_And_Ascii_Group_Spellings_For_An_Addition()
    {
        using var fixture = new CatalogueFixture();
        var before = await fixture.ReadAsync();
        fixture.Execute(InsertAdditionSql);
        fixture.Execute("""
            UPDATE item SET track_number=0,disc_number=1,is_compilation=0 WHERE item_pid=6;
            UPDATE item_extra SET sort_title=NULL,comment=NULL,track_count=0,disc_count=0,year=0 WHERE item_pid=6;
            UPDATE album SET album='ALBUM';
            UPDATE album_artist SET album_artist='ARTIST';
            UPDATE item_artist SET item_artist='ARTIST';
            UPDATE composer SET composer='COMPOSER';
            UPDATE genre SET genre='GENRE';
            """);

        var addition = Addition();
        foreach (var key in new[] { MediaKeys.TrackNumber, MediaKeys.TrackCount, MediaKeys.DiscNumber,
            MediaKeys.DiscCount, MediaKeys.Year, MediaKeys.IsCompilation, MediaKeys.Comment })
        {
            addition.Metadata.Item.Remove(key);
        }

        addition.Metadata.Item[MediaKeys.SortTitle] = "Added";
        // Milliseconds are recalculated from the audio samples rather than copied from the item scalar.
        addition.Metadata.Item[MediaKeys.TotalMilliseconds] = 1001L;
        var after = await fixture.ReadAsync();
        Assert.DoesNotThrow(() => SyncEngine.VerifyResult(
            before,
            after,
            [addition],
            []));
    }

    [Test]
    public async Task Rejects_Changes_To_Protected_Track_Metadata_And_Group_Membership()
    {
        using var fixture = new CatalogueFixture();
        fixture.Execute(OtherGroupsSql);
        var before = await fixture.ReadAsync();
        fixture.Execute("UPDATE item SET album_pid=13,track_number=99 WHERE item_pid=5;");
        var after = await fixture.ReadAsync();
        CatalogueAssertions.RejectsUnrequestedChanges(before, after);
    }

    private static PreparedTrack Addition()
    {
        var metadata = new Mp3Metadata(
            Plist.Dict(
                (MediaKeys.Title, "Added"),
                (MediaKeys.Album, "Album"),
                (MediaKeys.AlbumArtist, "Artist"),
                (MediaKeys.Artist, "Artist"),
                (MediaKeys.Composer, "Composer"),
                (MediaKeys.Genre, "Genre"),
                (MediaKeys.SortTitle, "Added, The"),
                (MediaKeys.SortAlbum, "Album, The"),
                (MediaKeys.SortAlbumArtist, "Artist, The"),
                (MediaKeys.SortArtist, "Performer, The"),
                (MediaKeys.SortComposer, "Composer, The"),
                (MediaKeys.Comment, "Comment"),
                (MediaKeys.TrackNumber, 3L),
                (MediaKeys.TrackCount, 9L),
                (MediaKeys.DiscNumber, 2L),
                (MediaKeys.DiscCount, 3L),
                (MediaKeys.Year, 2020L),
                (MediaKeys.IsCompilation, true),
                (MediaKeys.TotalMilliseconds, 1000L)),
            Plist.Dict(
                (MediaKeys.AudioFormat, 301L),
                (MediaKeys.SampleRate, 44100.0),
                (MediaKeys.BitRate, 128L),
                (MediaKeys.Duration, 44100L)),
            "Album",
            "Artist");

        return new PreparedTrack(
            new SourceTrack(
                "added.mp3",
                5,
                DateTime.UnixEpoch,
                "hash"),
            string.Empty,
            metadata,
            106,
            1012,
            1011);
    }
}
