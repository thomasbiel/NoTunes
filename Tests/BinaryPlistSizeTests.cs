using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class BinaryPlistSizeTests
{
    [Test]
    public void Measures_The_Independent_Atc_Ping_Fixture_Exactly()
    {
        Assert.That(BinaryPlistSize.Measure(Plist.Dict(("Command", "Ping"), ("Session", 1L))), Is.EqualTo(73));
    }

    [Test]
    public void Accounts_For_Extended_Lengths_References_And_Offsets(
        [Values(14, 15, 255, 256, 65535, 65536)] int length,
        [Values] bool unicode)
    {
        var text = new string(unicode ? '界' : 'a', length);
        var value = Plist.Dict(
            ("Text", text),
            ("Data", new byte[length]),
            ("References", Enumerable.Repeat<object>(true, Math.Min(length, 65536)).ToArray()),
            ("Numbers", new object[] { -1L, 0, 255L, 256L, 65535L, 65536L, long.MaxValue, long.MinValue }),
            ("Other", new object[] { false, 1.25, DateTime.UnixEpoch, new object[0], Plist.Dict() }));

        var size = checked((int)BinaryPlistSize.Measure(value));
        var bytes = BinaryPlist.WriteWithinLimit(value, size);
        Assert.That(bytes, Has.Length.EqualTo(size));
        Assert.That(BinaryPlist.WriteWithinLimit(value, size - 1), Is.Null);
        var result = BinaryPlist.Read(bytes!).AsDict();
        Assert.That(result.Text("Text"), Is.EqualTo(text));
        Assert.That((byte[])result["Data"], Has.Length.EqualTo(length));
    }

    [Test]
    public void Enforces_The_Object_And_Depth_Limits()
    {
        Assert.Throws<InvalidDataException>(() => BinaryPlistSize.Measure(
            Enumerable.Repeat<object>(true, Plist.MaxObjectCount).ToArray()));

        object nested = true;
        for (var depth = 0; depth <= Plist.MaxNestingDepth; depth++)
        {
            nested = new[] { nested };
        }

        Assert.Throws<InvalidDataException>(() => BinaryPlistSize.Measure(nested));
        Assert.Throws<InvalidDataException>(() => BinaryPlistSize.Measure(new object()));
    }
}
