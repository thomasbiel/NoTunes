using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class MusicGroupResolverTests
{
    [Test]
    public async Task Resolves_The_Exact_Boney_M_Name_Despite_A_Shared_Sync_Id([Values] bool dot)
    {
        const long sharedId = 6703696635933110658;
        var rows = new[]
        {
            Artist(11, sharedId, "Boney M"),
            Artist(22, sharedId, "Boney M.")
        };

        var resolver = new MusicGroupResolver(
            MusicGroupKind.AlbumArtist,
            rows,
            new Dictionary<string, long> { ["Boney M"] = 0, ["Boney M."] = 0 },
            new Dictionary<string, string?>(),
            new HashSet<long> { sharedId },
            (_, _) => throw new AssertionException("A unique name must not require user input."));

        var name = dot ? "Boney M." : "Boney M";
        var result = await resolver.ResolveAsync(
            name,
            name,
            "new-song.mp3",
            null,
            CancellationToken.None);

        Assert.That(result.Name, Is.EqualTo(name));
        Assert.That(result.Id, Is.EqualTo(sharedId));
    }

    [Test]
    public async Task Asks_For_Duplicate_Names_And_Reuses_The_Choice_For_The_Group([Values] bool newName)
    {
        var chosen = newName ? "Separate Artist" : "Chosen Artist";
        var calls = 0;
        var occupied = new HashSet<long> { 1011, 1022, 1023 };
        var resolver = new MusicGroupResolver(
            MusicGroupKind.AlbumArtist,
            [
                Artist(11, 1011, "Artist") with { SortName = "First" },
                Artist(22, 1022, "ARTIST") with { SortName = "Second" },
                Artist(23, 1023, "Chosen Artist") with { SortName = "Chosen Sort" }
            ],
            new Dictionary<string, long> { ["Artist"] = 0, ["Chosen Artist"] = 1023 },
            new Dictionary<string, string?> { ["Artist"] = null, ["Chosen Artist"] = "Chosen Sort" },
            occupied,
            (description, _) =>
            {
                calls++;
                Assert.That(description, Does.Contain("first.mp3").And.Contain("1011").And.Contain("1022"));
                return ValueTask.FromResult<string?>(chosen);
            });

        var first = await resolver.ResolveAsync(
            "Artist",
            "Source Sort",
            "first.mp3",
            null,
            CancellationToken.None);

        var second = await resolver.ResolveAsync(
            "artist",
            "Different Source Sort",
            "second.mp3",
            null,
            CancellationToken.None);

        Assert.That(calls, Is.EqualTo(1));
        Assert.That(second, Is.EqualTo(first));
        Assert.That(first.Name, Is.EqualTo(chosen));
        Assert.That(first.SortName, Is.EqualTo(newName ? chosen : "Chosen Sort"));
        Assert.That(first.Id, newName ? Is.Not.AnyOf(1011L, 1022L, 1023L) : Is.EqualTo(1023));
        Assert.That(occupied, Does.Contain(first.Id));
    }

    [Test]
    public async Task Does_Not_Rekey_An_Existing_Group_With_Zero_Sync_Id()
    {
        var resolver = new MusicGroupResolver(
            MusicGroupKind.AlbumArtist,
            [Artist(11, 0, "Artist")],
            new Dictionary<string, long>(),
            new Dictionary<string, string?>(),
            new HashSet<long>(),
            (_, _) => ValueTask.FromResult<string?>("Separate Artist"));

        var result = await resolver.ResolveAsync(
            "Artist",
            "Artist",
            "one.mp3",
            null,
            CancellationToken.None);

        Assert.That(result.Name, Is.EqualTo("Separate Artist"));
        Assert.That(result.Id, Is.Not.Zero);
    }

    [Test]
    public async Task Repeats_The_Question_For_Invalid_Or_Still_Ambiguous_Names()
    {
        var answers = new Queue<string>(["bad\0name", "Artist", "New Artist"]);
        var resolver = new MusicGroupResolver(
            MusicGroupKind.Artist,
            null,
            null,
            new Dictionary<string, string?> { ["Artist"] = null },
            new HashSet<long>(),
            (_, _) => ValueTask.FromResult<string?>(answers.Dequeue()));

        var result = await resolver.ResolveAsync(
            "Artist",
            "Artist",
            "one.mp3",
            null,
            CancellationToken.None);

        Assert.That(result.Name, Is.EqualTo("New Artist"));
        Assert.That(answers, Is.Empty);
    }

    [Test]
    public async Task Stops_Without_An_Answer_Or_On_Cancellation([Values] bool endOfInput)
    {
        var resolver = new MusicGroupResolver(
            MusicGroupKind.Composer,
            null,
            null,
            new Dictionary<string, string?> { ["Composer"] = null },
            new HashSet<long>(),
            (_, _) => ValueTask.FromResult(endOfInput ? null : string.Empty));

        var failure = await Assert.CatchAsync<Exception>(() => resolver.ResolveAsync(
            "Composer",
            "Composer",
            "affected.mp3",
            null,
            CancellationToken.None));

        Assert.That(failure, endOfInput ? Is.TypeOf<InvalidDataException>() : Is.TypeOf<OperationCanceledException>());
        if (endOfInput) Assert.That(failure!.Message, Does.Contain("affected.mp3").And.Contain("interactive terminal"));
    }

    private static CatalogueGroup Artist(long rowId, long syncId, string name)
    {
        return new CatalogueGroup(
            rowId,
            syncId,
            name,
            name,
            null,
            null);
    }
}
