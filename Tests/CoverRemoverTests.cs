using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class CoverRemoverTests
{
    [Test]
    public async Task Removes_All_Covers_And_Preserves_Audio_And_Other_Tags(
        [Values] bool invalidPicture,
        [Values(1, 2)] int pictureCount)
    {
        using var source = new SyncWorkspace();
        var path = SourceTestData.WriteTrack(source.Root, "song.MP3", file =>
        {
            file.Tag.Title = "Unchanged title";
            file.Tag.Album = "Unchanged album";
            file.Tag.Performers = ["Unchanged artist"];
            file.Tag.AlbumArtists = ["Unchanged album artist"];
            file.Tag.Comment = "Unchanged comment";
            file.Tag.Genres = ["Unchanged genre"];
            file.Tag.Track = 3;
            file.Tag.Year = 2020;
            var picture = FeatureTestData.FrontCover(invalidPicture ? [1, 2, 3, 4] : null);
            var back = FeatureTestData.FrontCover();
            back.Type = TagLib.PictureType.BackCover;
            file.Tag.Pictures = pictureCount == 1 ? [picture] : [picture, back];
            var custom = TagLib.Id3v2.UserTextInformationFrame.Get(
                (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true),
                "Unchanged custom tag",
                true);

            custom.Text = ["Unchanged value"];
        });

        var neighbor = SourceTestData.WriteCoveredTrack(source.Root, "other.mp3");
        var neighborBytes = await File.ReadAllBytesAsync(neighbor);
        var audioBefore = AudioHash(path);
        var oldModified = DateTime.UnixEpoch.AddYears(30);
        File.SetLastWriteTimeUtc(path, oldModified);
        var scanned = await SourceTestData.ScanAsync(source.Root);

        var result = await RemoveAsync(path);

        Assert.That(result.Path, Is.EqualTo(path));
        Assert.That(result.RemovedCovers, Is.EqualTo(pictureCount));
        Assert.That(AudioHash(path), Is.EqualTo(audioBefore));
        Assert.That(await File.ReadAllBytesAsync(neighbor), Is.EqualTo(neighborBytes));
        Assert.That(File.GetLastWriteTimeUtc(path), Is.Not.EqualTo(oldModified));
        using var verified = TagLib.File.Create(path);
        Assert.That(verified.Tag.Pictures, Is.Empty);
        Assert.That(verified.Tag.Title, Is.EqualTo("Unchanged title"));
        Assert.That(verified.Tag.Album, Is.EqualTo("Unchanged album"));
        Assert.That(verified.Tag.FirstPerformer, Is.EqualTo("Unchanged artist"));
        Assert.That(verified.Tag.FirstAlbumArtist, Is.EqualTo("Unchanged album artist"));
        Assert.That(verified.Tag.Comment, Is.EqualTo("Unchanged comment"));
        Assert.That(verified.Tag.FirstGenre, Is.EqualTo("Unchanged genre"));
        Assert.That(verified.Tag.Track, Is.EqualTo(3));
        Assert.That(verified.Tag.Year, Is.EqualTo(2020));
        var customAfter = TagLib.Id3v2.UserTextInformationFrame.Get(
            (TagLib.Id3v2.Tag)verified.GetTag(TagLib.TagTypes.Id3v2, false),
            "Unchanged custom tag",
            false);

        Assert.That(customAfter.Text, Is.EqualTo(["Unchanged value"]));
        var rescanned = await SourceTestData.ScanAsync(source.Root);
        Assert.That(
            rescanned.Tracks.Single(track => track.RelativePath == "song.MP3").Hash,
            Is.Not.EqualTo(scanned.Tracks.Single(track => track.RelativePath == "song.MP3").Hash));
    }

    [Test]
    public async Task Leaves_A_File_Without_Covers_Byte_Identical()
    {
        using var source = new SyncWorkspace();
        var path = SourceTestData.WriteTrack(source.Root, "song.mp3", file => file.Tag.Pictures = []);
        var bytes = await File.ReadAllBytesAsync(path);
        var modified = File.GetLastWriteTimeUtc(path);

        var result = await RemoveAsync(path);

        Assert.That(result.RemovedCovers, Is.Zero);
        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(bytes));
        Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(modified));
    }

    [Test]
    public async Task Removes_Pictures_From_Both_Id3_And_Ape_Tags()
    {
        using var source = new SyncWorkspace();
        var path = SourceTestData.WriteTrack(source.Root, "multiple-tags.mp3", file =>
        {
            file.GetTag(TagLib.TagTypes.Id3v2, true).Pictures = [FeatureTestData.FrontCover()];
            file.GetTag(TagLib.TagTypes.Ape, true).Pictures = [FeatureTestData.FrontCover([1, 2, 3, 4])];
        });

        var audioBefore = AudioHash(path);

        var result = await RemoveAsync(path);

        Assert.That(result.RemovedCovers, Is.EqualTo(2));
        Assert.That(AudioHash(path), Is.EqualTo(audioBefore));
        using var verified = TagLib.File.Create(path);
        Assert.That(verified.GetTag(TagLib.TagTypes.Id3v2, false)?.Pictures ?? [], Is.Empty);
        Assert.That(verified.GetTag(TagLib.TagTypes.Ape, false)?.Pictures ?? [], Is.Empty);
    }

    [Test]
    public async Task Rejects_A_Fake_Mp3_Without_Changing_It()
    {
        using var source = new SyncWorkspace();
        var path = Path.Combine(source.Root, "fake.mp3");
        await File.WriteAllTextAsync(path, "Not an MP3");
        var bytes = await File.ReadAllBytesAsync(path);

        await Assert.CatchAsync<Exception>(() => RemoveAsync(path));

        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(bytes));
    }

    [Test]
    public async Task Honors_Cancellation_Before_Opening_The_File()
    {
        using var source = new SyncWorkspace();
        var path = SourceTestData.WriteCoveredTrack(source.Root, "covered.mp3");
        var bytes = await File.ReadAllBytesAsync(path);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Assert.CatchAsync<OperationCanceledException>(() => RemoveAsync(path, canceled.Token));

        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(bytes));
    }

    [Test]
    public async Task Restores_The_Exact_Original_After_A_Partial_Write_Or_Cancellation([Values] bool cancel)
    {
        using var source = new SyncWorkspace();
        var path = SourceTestData.WriteCoveredTrack(source.Root, "covered.mp3");
        var bytes = await File.ReadAllBytesAsync(path);
        var modified = File.GetLastWriteTimeUtc(path);
        using var cancellation = new CancellationTokenSource();
        var fileSystem = new FailingEditFileSystem(() =>
        {
            if (cancel)
            {
                cancellation.Cancel();
            }
            else
            {
                throw new IOException("Injected metadata write failure.");
            }
        });

        var remover = new CoverRemover(fileSystem);
        var failure = await Assert.CatchAsync<Exception>(() => remover.RemoveAsync(path, cancellation.Token));

        Assert.That(failure, cancel ? Is.InstanceOf<OperationCanceledException>() : Is.TypeOf<IOException>());
        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(bytes));
        Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(modified));
        Assert.That(fileSystem.Session.Disposed, Is.True);
    }

    [Test]
    [Platform("Linux")]
    public async Task Rejects_A_Symbolic_Link_Without_Changing_Its_Target([Values] bool linkedDirectory)
    {
        using var source = new SyncWorkspace();
        var actualDirectory = Directory.CreateDirectory(Path.Combine(source.Root, "actual"));
        var path = SourceTestData.WriteCoveredTrack(actualDirectory.FullName, "covered.mp3");

        var bytes = await File.ReadAllBytesAsync(path);
        var link = Path.Combine(source.Root, linkedDirectory ? "linked" : "linked.mp3");
        try
        {
            if (linkedDirectory)
            {
                Directory.CreateSymbolicLink(link, actualDirectory.FullName);
            }
            else
            {
                File.CreateSymbolicLink(link, path);
            }
        }
        catch (UnauthorizedAccessException)
        {
            Assert.Ignore("This environment cannot create symbolic links.");
        }

        var selected = linkedDirectory ? Path.Combine(link, "covered.mp3") : link;

        await Assert.ThrowsAsync<IOException>(() => RemoveAsync(selected));

        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(bytes));
    }

    private static async Task<CoverRemovalResult> RemoveAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var services = new ServiceCollection().AddNoTunes(TextWriter.Null, TextWriter.Null)
            .BuildServiceProvider();

        return await services.GetRequiredService<CoverRemover>().RemoveAsync(path, cancellationToken);
    }

    private static string AudioHash(string path)
    {
        using var file = TagLib.File.Create(path);
        var bytes = File.ReadAllBytes(path);
        var start = checked((int)file.InvariantStartPosition);
        var length = checked((int)(file.InvariantEndPosition - file.InvariantStartPosition));
        return Convert.ToHexString(SHA1.HashData(bytes.AsSpan(start, length)));
    }
}
