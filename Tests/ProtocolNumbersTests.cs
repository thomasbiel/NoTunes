using System.Globalization;
using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class ProtocolNumbersTests
{
    [TestCase("0", 0L)]
    [TestCase("00042", 42L)]
    [TestCase("9223372036854775807", long.MaxValue)]
    public void Parses_Nonnegative_Decimal_Values_Within_The_Int64_Range(string text, long expected)
    {
        Assert.That(ProtocolNumbers.TryParseNonNegativeInt64(text, out var value), Is.True);
        Assert.That(value, Is.EqualTo(expected));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("-1")]
    [TestCase("+1")]
    [TestCase(" 1")]
    [TestCase("1 ")]
    [TestCase("1.0")]
    [TestCase("1,000")]
    [TestCase("1e3")]
    [TestCase("9223372036854775808")]
    public void Rejects_Missing_Formatted_Signed_Or_Overflowing_Values(string? text)
    {
        Assert.That(ProtocolNumbers.TryParseNonNegativeInt64(text, out var value), Is.False);
        Assert.That(value, Is.Zero);
    }

    [TestCase("de-DE")]
    [TestCase("ar-SA")]
    public void Uses_The_Protocol_Format_Regardless_Of_The_Current_Culture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Assert.That(ProtocolNumbers.TryParseNonNegativeInt64("1234", out var value), Is.True);
            Assert.That(value, Is.EqualTo(1234));
            Assert.That(ProtocolNumbers.TryParseNonNegativeInt64("1,234", out _), Is.False);
            Assert.That(ProtocolNumbers.TryParseNonNegativeInt64("1.234", out _), Is.False);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
