using Microsoft.Extensions.DependencyInjection;
using NoTunes.Music;
using NoTunes.Platform;

namespace NoTunes.Tests;

internal static class SourceTestData
{
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
        bool artworkOnly = false)
    {
        await using var services = CreateServiceProvider();
        var library = await services.GetRequiredService<SourceScanner>().ScanAsync(root, CancellationToken.None);
        return await PrepareAsync(
            library,
            catalogue,
            output,
            services.GetRequiredService<ISourceFileSystem>(),
            prompt,
            artworkOnly);
    }

    public static async Task<IReadOnlyList<PreparedTrack>> PrepareAsync(
        SourceLibrary library,
        DeviceCatalogue catalogue,
        SyncWorkspace output,
        GroupNamePrompt? prompt = null,
        bool artworkOnly = false)
    {
        await using var services = CreateServiceProvider();
        return await PrepareAsync(
            library,
            catalogue,
            output,
            services.GetRequiredService<ISourceFileSystem>(),
            prompt,
            artworkOnly);
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
        bool artworkOnly)
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
            artworkOnly,
            prompt: prompt);
    }
}
