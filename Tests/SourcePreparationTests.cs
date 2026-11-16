using Microsoft.Extensions.DependencyInjection;
using NoTunes.Music;
using NoTunes.Platform;
using NUnit.Framework;

namespace NoTunes.Tests;

public sealed class SourcePreparationTests
{
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
        Assert.ThrowsAsync<IOException>(() => SourcePreparation.PrepareAsync(
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
}
