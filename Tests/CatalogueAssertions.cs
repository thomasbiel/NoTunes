using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

internal static class CatalogueAssertions
{
    public static void RejectsUnrequestedChanges(DeviceCatalogue before, DeviceCatalogue after)
    {
        Assert.Throws<IOException>(() => SyncEngine.RequireUnchanged(before, after));
        Assert.Throws<IOException>(() => SyncEngine.VerifyResult(
            before,
            after with { Anchor = before.Anchor + 1 },
            [],
            []));
    }

    public static void AcceptsUnchanged(DeviceCatalogue before, DeviceCatalogue after)
    {
        Assert.DoesNotThrow(() => SyncEngine.RequireUnchanged(before, after));
        Assert.DoesNotThrow(() => SyncEngine.VerifyResult(
            before,
            after with { Anchor = before.Anchor + 1 },
            [],
            []));
    }
}
