using NoTunes.Cli;
using NUnit.Framework;

namespace NoTunes.Tests;

public sealed class SourceSettingsTests
{
    private string directory = null!;
    private string config = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), "NoTunes-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        config = Path.Combine(directory, "test.config");
    }

    [Test]
    public void ReadsTheUncPathWithoutChangingItsBackslashes()
    {
        File.WriteAllText(config, """
            <configuration><appSettings>
              <add key="MusicSourceFolder" value="\\192.168.0.150\music" />
            </appSettings></configuration>
            """);
        Assert.That(SourceSettings.ResolveFolder(null, config), Is.EqualTo(@"\\192.168.0.150\music"));
    }

    [Test]
    public void ExplicitFolderOverridesConfigurationWithoutReadingIt() =>
        Assert.That(SourceSettings.ResolveFolder(@"D:\Music", config), Is.EqualTo(@"D:\Music"));

    [TestCase("<configuration><appSettings /></configuration>")]
    [TestCase("<configuration><appSettings><add key=\"MusicSourceFolder\" value=\" \" /></appSettings></configuration>")]
    [TestCase("<configuration>")]
    public void RejectsMissingEmptyOrMalformedConfiguration(string xml)
    {
        File.WriteAllText(config, xml);
        Assert.Throws<InvalidDataException>(() => SourceSettings.ResolveFolder(null, config));
    }

    [Test]
    public void RejectsAnExplicitEmptyFolderInsteadOfFallingBackToConfiguration() =>
        Assert.Throws<ArgumentException>(() => SourceSettings.ResolveFolder(" ", config));

    [TearDown]
    public void TearDown() => Directory.Delete(directory, recursive: true);
}
