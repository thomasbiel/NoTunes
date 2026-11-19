using System.Globalization;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class DisplayFormatTests
{
    [TestCase(0L, "0 B")]
    [TestCase(999L, "999 B")]
    [TestCase(1000L, "1.0 KB")]
    [TestCase(1250L, "1.3 KB")]
    [TestCase(999949L, "999.9 KB")]
    [TestCase(999950L, "1.0 MB")]
    [TestCase(1234567L, "1.2 MB")]
    [TestCase(45600000L, "45.6 MB")]
    [TestCase(1000000000L, "1.0 GB")]
    [TestCase(1000000000000L, "1.0 TB")]
    [TestCase(long.MaxValue, "9.2 EB")]
    public void Formats_Decimal_Byte_Units_With_One_Decimal_Place(long value, string expected)
    {
        Assert.That(DisplayFormat.Bytes(value), Is.EqualTo(expected));
    }

    [Test]
    public void Handles_Unsigned_Device_Sizes_And_Preserves_Unrecognized_Values()
    {
        Assert.That(DisplayFormat.Bytes(ulong.MaxValue), Is.EqualTo("18.4 EB"));
        Assert.That(DisplayFormat.Bytes("12345"), Is.EqualTo("12.3 KB"));
        Assert.That(DisplayFormat.Bytes("unknown"), Is.EqualTo("unknown"));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayFormat.Bytes(-1L));
    }

    [TestCase("en-US")]
    [TestCase("de-DE")]
    public void Keeps_English_Cli_Number_Formatting_Independent_Of_The_Current_Culture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Assert.That(DisplayFormat.Number(1234567), Is.EqualTo("1,234,567"));
            Assert.That(DisplayFormat.Bytes(12345678L), Is.EqualTo("12.3 MB"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
