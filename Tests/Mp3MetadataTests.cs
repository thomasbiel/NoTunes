using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

public sealed class Mp3MetadataTests
{
    [Test]
    public void Preserves_Compilation_Flags_And_Explicit_Album_Artists()
    {
        using var workspace = new SyncWorkspace();
        var path = Path.Combine(workspace.Root, "compilation.mp3");
        File.Copy(SyncTestData.FixturePath, path);
        using (var file = TagLib.File.Create(path))
        {
            var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true);
            id3.IsCompilation = true;
            file.Tag.Performers = ["Track Artist"];
            file.Tag.AlbumArtists = ["Verschiedene Künstler"];
            file.Tag.AlbumArtistsSort = ["Künstler, Verschiedene"];
            file.Save();
        }

        var metadata = Mp3Metadata.Read(path, "compilation.mp3");
        Assert.That(metadata.Item.GetValueOrDefault(MediaKeys.IsCompilation), Is.True);
        Assert.That(metadata.Item[MediaKeys.AlbumArtist], Is.EqualTo("Verschiedene Künstler"));
        Assert.That(metadata.Item[MediaKeys.SortAlbumArtist], Is.EqualTo("Künstler, Verschiedene"));
    }

    [Test]
    public void Rejects_An_Unqualified_Compilation_Without_An_Album_Artist()
    {
        using var workspace = new SyncWorkspace();
        var path = Path.Combine(workspace.Root, "compilation.mp3");
        File.Copy(SyncTestData.FixturePath, path);
        using (var file = TagLib.File.Create(path))
        {
            ((TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true)).IsCompilation = true;
            file.Tag.AlbumArtists = [];
            file.Save();
        }

        var error = Assert.Throws<InvalidDataException>(() => Mp3Metadata.Read(path, "compilation.mp3"));
        Assert.That(error!.Message, Does.Contain("album artist"));
    }

    [TestCase(false, "Artist, The")]
    [TestCase(true, "Album Artist")]
    public void Uses_The_Performer_Sort_Only_For_An_Implicit_Album_Artist(bool explicitAlbumArtist, string expected)
    {
        using var workspace = new SyncWorkspace();
        var path = Path.Combine(workspace.Root, "sorted.mp3");
        File.Copy(SyncTestData.FixturePath, path);
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Performers = ["The Artist"];
            file.Tag.PerformersSort = ["Artist, The"];
            file.Tag.AlbumArtists = explicitAlbumArtist ? ["Album Artist"] : [];
            file.Tag.AlbumArtistsSort = [];
            file.Save();
        }

        var metadata = Mp3Metadata.Read(path, "sorted.mp3");
        Assert.That(metadata.Item[MediaKeys.SortArtist], Is.EqualTo("Artist, The"));
        Assert.That(metadata.Item[MediaKeys.SortAlbumArtist], Is.EqualTo(expected));
    }
}
