using NoTunes.Apple;
using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class SyncJournalTests
{
    [Test]
    public void Retains_An_Unverified_Operation_And_Refuses_Blind_Replay()
    {
        using var workspace = new SyncWorkspace();
        var plan = new SyncPlan(
            "phone",
            7,
            [],
            [new(
                101,
                "Old",
                "old.mp3",
                5)],
            0);
        using (var journal = new SyncJournal(workspace.Root, "phone"))
            journal.Start(
                new LibraryIdentity("id", "binding"),
                plan,
                [1, 2],
                [3, 4]);
        Assert.Throws<IOException>(() => new SyncJournal(workspace.Root, "phone"));
        var pending = Directory.GetFiles(workspace.Root, "pending.json", SearchOption.AllDirectories).Single();
        Assert.That(File.ReadAllText(pending), Does.Contain("binding").And.Contain("Operation"));
    }

    [Test]
    public void Completes_A_Journal_Only_After_Explicit_Verification()
    {
        using var workspace = new SyncWorkspace();
        using (var journal = new SyncJournal(workspace.Root, "phone"))
        {
            journal.Start(
                new LibraryIdentity("id", "binding"),
                new SyncPlan(
                    "phone",
                    7,
                    [],
                    [],
                    0),
                [1],
                [2]);
            journal.Complete();
        }

        using var next = new SyncJournal(workspace.Root, "phone");
        Assert.That(Directory.GetFiles(workspace.Root, "pending.json", SearchOption.AllDirectories), Is.Empty);
        Assert.That(Directory.GetFiles(workspace.Root, "verified-*.json", SearchOption.AllDirectories), Has.Length.EqualTo(1));
    }

    [Test]
    public void Normalized_Udids_Share_The_Same_Exclusive_Lease()
    {
        using var workspace = new SyncWorkspace();
        using var journal = new SyncJournal(workspace.Root, "00008030-001122334455AABB");
        Assert.Throws<IOException>(() => new SyncJournal(workspace.Root, "00008030001122334455aabb"));
    }
}
