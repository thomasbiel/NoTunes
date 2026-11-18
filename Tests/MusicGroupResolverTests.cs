using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class MusicGroupResolverTests
{
    [TestCase("Boney M.", "Boney M")]
    [TestCase("R.E.M.", "REM")]
    public async Task Chooses_The_First_Receiver_Row_For_A_Dotted_Alias(string canonical, string alias)
    {
        const long sharedId = 1011;
        var resolver = CreateResolver(
            rows: [Artist(22, sharedId, alias), Artist(11, sharedId, canonical)],
            occupied: new HashSet<long> { sharedId },
            prompt: (_, _) => throw new AssertionException("A dotted alias must be resolved automatically."));

        var result = await ResolveAsync(resolver, alias, sourcePath: "new-song.mp3");
        Assert.That(result.Name, Is.EqualTo(canonical));
        Assert.That(result.SortName, Is.EqualTo(canonical));
        Assert.That(result.Id, Is.EqualTo(sharedId));
    }

    [Test]
    public async Task Uses_The_Receiver_Spelling_For_Either_Requested_Boney_M_Alias([Values] bool dot)
    {
        const long sharedId = 6703696635933110658;
        var rows = new[]
        {
            Artist(11, sharedId, "Boney M"),
            Artist(22, sharedId, "Boney M.")
        };

        var resolver = CreateResolver(
            rows: rows,
            ids: new Dictionary<string, long> { ["Boney M"] = 0, ["Boney M."] = 0 },
            occupied: new HashSet<long> { sharedId },
            prompt: (_, _) => throw new AssertionException("A unique name must not require user input."));

        var name = dot ? "Boney M." : "Boney M";
        var result = await ResolveAsync(resolver, name, sourcePath: "new-song.mp3");
        Assert.That(result.Name, Is.EqualTo("Boney M"));
        Assert.That(result.Id, Is.EqualTo(sharedId));
    }

    [Test]
    public async Task Asks_When_Unrelated_Names_Share_An_Identity([Values] bool newName)
    {
        const long sharedId = 1011;
        var calls = 0;
        var chosen = newName ? "Separate Artist" : "Artist";
        var resolver = CreateResolver(
            rows: [Artist(11, sharedId, "Artist"), Artist(22, sharedId, "Other Artist")],
            occupied: new HashSet<long> { sharedId },
            prompt: (description, _) =>
            {
                calls++;
                Assert.That(description, Does.Contain("Other Artist").And.Contain("row 11").And.Contain("row 22"));
                return ValueTask.FromResult<string?>(chosen);
            });

        var result = await ResolveAsync(resolver, "Other Artist");
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(result.Name, Is.EqualTo(chosen));
        Assert.That(result.Id, newName ? Is.Not.EqualTo(sharedId) : Is.EqualTo(sharedId));
    }

    [Test]
    public async Task Asks_For_Duplicate_Names_And_Reuses_The_Choice_For_The_Group([Values] bool newName)
    {
        var chosen = newName ? "Separate Artist" : "Chosen Artist";
        var calls = 0;
        var occupied = new HashSet<long>
        {
            1011,
            1022,
            1023
        };

        var resolver = CreateResolver(
            rows:
            [
                Artist(11, 1011, "Artist") with { SortName = "First" },
                Artist(22, 1022, "ARTIST") with { SortName = "Second" },
                Artist(23, 1023, "Chosen Artist") with { SortName = "Chosen Sort" }
            ],
            ids: new Dictionary<string, long> { ["Artist"] = 0, ["Chosen Artist"] = 1023 },
            sorts: new Dictionary<string, string?> { ["Artist"] = null, ["Chosen Artist"] = "Chosen Sort" },
            occupied: occupied,
            prompt: (description, _) =>
            {
                calls++;
                Assert.That(description, Does.Contain("first.mp3").And.Contain("1011").And.Contain("1022"));
                return ValueTask.FromResult<string?>(chosen);
            });

        var first = await ResolveAsync(
            resolver,
            "Artist",
            sortName: "Source Sort",
            sourcePath: "first.mp3");

        var second = await ResolveAsync(
            resolver,
            "artist",
            sortName: "Different Source Sort",
            sourcePath: "second.mp3");

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
        var resolver = CreateResolver(
            rows: [Artist(11, 0, "Artist")],
            prompt: (_, _) => ValueTask.FromResult<string?>("Separate Artist"));

        var result = await ResolveAsync(resolver, "Artist", sourcePath: "one.mp3");
        Assert.That(result.Name, Is.EqualTo("Separate Artist"));
        Assert.That(result.Id, Is.Not.Zero);
    }

    [Test]
    public async Task Repeats_The_Question_For_Invalid_Or_Still_Ambiguous_Names()
    {
        var answers = new Queue<string>(["bad\0name", "Artist", "New Artist"]);
        var resolver = CreateResolver(
            kind: MusicGroupKind.Artist,
            sorts: new Dictionary<string, string?> { ["Artist"] = null },
            prompt: (_, _) => ValueTask.FromResult<string?>(answers.Dequeue()));

        var result = await ResolveAsync(resolver, "Artist", sourcePath: "one.mp3");
        Assert.That(result.Name, Is.EqualTo("New Artist"));
        Assert.That(answers, Is.Empty);
    }

    [Test]
    public async Task Stops_Without_An_Answer_Or_On_Cancellation([Values] bool endOfInput)
    {
        var resolver = CreateResolver(
            kind: MusicGroupKind.Composer,
            sorts: new Dictionary<string, string?> { ["Composer"] = null },
            prompt: (_, _) => ValueTask.FromResult(endOfInput ? null : string.Empty));

        var failure = await Assert.CatchAsync<Exception>(() => ResolveAsync(resolver, "Composer"));
        Assert.That(failure, endOfInput ? Is.TypeOf<InvalidDataException>() : Is.TypeOf<OperationCanceledException>());
        if (endOfInput)
            Assert.That(failure!.Message, Does.Contain("affected.mp3").And.Contain("interactive terminal"));
    }

    [Test]
    public async Task Does_Not_Resolve_A_Shared_Album_Through_Another_Album_Artist()
    {
        const long sharedId = 1012;
        var resolver = CreateResolver(
            kind: MusicGroupKind.Album,
            rows: [new CatalogueGroup(
                12,
                sharedId,
                "Album",
                "Album",
                11,
                "First Artist"), new CatalogueGroup(
                    13,
                    sharedId,
                    "Album.",
                    "Album.",
                    22,
                    "Second Artist")],
            occupied: new HashSet<long> { sharedId },
            prompt: (_, _) => ValueTask.FromResult<string?>("Separate Album"));

        var result = await ResolveAsync(resolver, "Album.", albumArtist: "Second Artist");
        Assert.That(result.Name, Is.EqualTo("Separate Album"));
        Assert.That(result.Id, Is.Not.EqualTo(sharedId));
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

    private static MusicGroupResolver CreateResolver(
        MusicGroupKind kind = MusicGroupKind.AlbumArtist,
        IReadOnlyList<CatalogueGroup>? rows = null,
        IReadOnlyDictionary<string, long>? ids = null,
        IReadOnlyDictionary<string, string?>? sorts = null,
        ISet<long>? occupied = null,
        GroupNamePrompt? prompt = null)
    {
        return new MusicGroupResolver(
            kind,
            rows,
            ids ?? new Dictionary<string, long>(),
            sorts ?? new Dictionary<string, string?>(),
            occupied ?? new HashSet<long>(),
            prompt);
    }

    private static Task<ResolvedMusicGroup> ResolveAsync(
        MusicGroupResolver resolver,
        string name,
        string? sortName = null,
        string sourcePath = "affected.mp3",
        string? albumArtist = null)
    {
        return resolver.ResolveAsync(
            name,
            sortName ?? name,
            sourcePath,
            albumArtist,
            CancellationToken.None);
    }
}
