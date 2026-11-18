using Microsoft.Extensions.DependencyInjection;
using NoTunes.Music;
using NoTunes.Platform;

namespace NoTunes.Tests;

internal static class SourceTestData
{
    public static string WriteSyncTracks(
        string root,
        int count,
        bool artwork = false,
        Action<TagLib.File>? configure = null)
    {
        var path = WriteSyncTrack(
            root,
            "0000.mp3",
            artwork,
            configure);

        for (var index = 1; index < count; index++)
        {
            File.Copy(path, Path.Combine(root, $"{index:0000}.mp3"));
        }

        return path;
    }

    public static string WriteSyncTrack(
        string root,
        string name,
        bool artwork = false,
        Action<TagLib.File>? configure = null)
    {
        return WriteTrack(
            root,
            name,
            "Album",
            "Artist",
            file =>
            {
                file.Tag.Composers = ["Composer"];
                file.Tag.Genres = ["Genre"];
                if (artwork)
                {
                    file.Tag.Pictures = [FeatureTestData.FrontCover()];
                }

                configure?.Invoke(file);
            });

    }

    public static string WriteCoveredTrack(string root, string name = "covered.mp3", byte[]? coverBytes = null)
    {
        return WriteTrack(root, name, file => file.Tag.Pictures = [FeatureTestData.FrontCover(coverBytes)]);
    }

    public static string WriteTrack(
        string root,
        string name,
        string album,
        string artist,
        Action<TagLib.File>? configure = null)
    {
        return WriteTrack(root, name, file =>
        {
            file.Tag.Album = album;
            file.Tag.Performers = [artist];
            file.Tag.AlbumArtists = [artist];
            configure?.Invoke(file);
        });
    }

    public static string WriteTrack(string root, string name, Action<TagLib.File> configure)
    {
        var path = Path.Combine(root, name);
        File.Copy(SyncTestData.FixturePath, path);
        using var file = TagLib.File.Create(path);
        configure(file);
        file.Save();
        return path;
    }

    public static async Task<SourceLibrary> ScanAsync(string root)
    {
        await using var services = CreateServiceProvider();
        return await services.GetRequiredService<SourceScanner>().ScanAsync(root, CancellationToken.None);
    }

    public static async Task<IReadOnlyList<PreparedTrack>> PrepareAsync(
        string root,
        DeviceCatalogue catalogue,
        SyncWorkspace output,
        GroupNamePrompt? prompt = null,
        bool artworkOnly = false,
        Action<SkippedSourceFile>? onSkipped = null)
    {
        await using var services = CreateServiceProvider();
        var library = await services.GetRequiredService<SourceScanner>().ScanAsync(root, CancellationToken.None);
        return await PrepareAsync(
            library,
            catalogue,
            output,
            services.GetRequiredService<ISourceFileSystem>(),
            prompt,
            artworkOnly,
            onSkipped);
    }

    public static async Task<IReadOnlyList<PreparedTrack>> PrepareAsync(
        SourceLibrary library,
        DeviceCatalogue catalogue,
        SyncWorkspace output,
        GroupNamePrompt? prompt = null,
        bool artworkOnly = false,
        Action<SkippedSourceFile>? onSkipped = null)
    {
        await using var services = CreateServiceProvider();
        return await PrepareAsync(
            library,
            catalogue,
            output,
            services.GetRequiredService<ISourceFileSystem>(),
            prompt,
            artworkOnly,
            onSkipped);
    }

    private static ServiceProvider CreateServiceProvider()
    {
        return new ServiceCollection().AddNoTunes(TextWriter.Null, TextWriter.Null).BuildServiceProvider();
    }

    private static Task<IReadOnlyList<PreparedTrack>> PrepareAsync(
        SourceLibrary library,
        DeviceCatalogue catalogue,
        SyncWorkspace output,
        ISourceFileSystem fileSystem,
        GroupNamePrompt? prompt,
        bool artworkOnly,
        Action<SkippedSourceFile>? onSkipped)
    {
        var plan = new SyncPlan(
            "phone",
            catalogue.Anchor,
            library.Tracks,
            [],
            0);

        return SourcePreparation.PrepareAsync(
            library,
            plan,
            catalogue,
            fileSystem,
            output,
            CancellationToken.None,
            new SourcePreparationOptions { ArtworkOnly = artworkOnly, Prompt = prompt, OnSkipped = onSkipped });
    }
}
