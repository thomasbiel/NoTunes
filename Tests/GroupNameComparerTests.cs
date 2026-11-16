using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

public sealed class GroupNameComparerTests
{
    [TestCase("Artist", "ARTIST", true)]
    [TestCase("Björk", "Björk", true)]
    [TestCase("Björk", "BJÖRK", false)]
    [TestCase("Björk", "björk", false)]
    [TestCase("Album\0Artist", "ALBUM\0ARTIST", true)]
    public void Matches_Exact_Unicode_And_Ascii_Case_Variants(string left, string right, bool matches)
    {
        var comparer = GroupNameComparer.Instance;
        Assert.That(comparer.Equals(left, right), Is.EqualTo(matches));
        if (matches)
            Assert.That(comparer.GetHashCode(left), Is.EqualTo(comparer.GetHashCode(right)));
    }
}
