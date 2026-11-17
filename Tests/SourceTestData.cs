using Microsoft.Extensions.DependencyInjection;
using NoTunes.Music;
using NoTunes.Platform;

namespace NoTunes.Tests;

internal static class SourceTestData
{
    public static void WriteTrack(string root, string name, string album, string artist)
    {
        var path = Path.Combine(root, name);
        File.Copy(SyncTestData.FixturePath, path);
        using var file = TagLib.File.Create(path);
        file.Tag.Album = album;
        file.Tag.Performers = [artist];
        file.Tag.AlbumArtists = [artist];
        file.Save();
    }

    public static async Task<IReadOnlyList<PreparedTrack>> PrepareAsync(
        string root,
        DeviceCatalogue catalogue,
        SyncWorkspace output)
    {
        await using var services = new ServiceCollection().AddNoTunes(TextWriter.Null, TextWriter.Null)
            .BuildServiceProvider();
        var library = await services.GetRequiredService<SourceScanner>().ScanAsync(root, CancellationToken.None);
        var plan = new SyncPlan(
            "phone",
            catalogue.Anchor,
            library.Tracks,
            [],
            0);
        return await SourcePreparation.PrepareAsync(
            library,
            plan,
            catalogue,
            services.GetRequiredService<ISourceFileSystem>(),
            output,
            CancellationToken.None);
    }
}
