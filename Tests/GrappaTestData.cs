using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

internal static class GrappaTestData
{
    private const string FixtureName = "grappa-fixed-vectors.plist";

    public static byte[] Reply() => (byte[])Vectors()[0].AsDict()["reply"];

    public static object[] Vectors()
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", FixtureName);
        return (object[])Plist.Parse(File.ReadAllBytes(path));
    }
}
