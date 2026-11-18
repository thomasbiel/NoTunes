using NoTunes.Cli;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class SourceSettingsTests
{
    private string directory = null!;
    private string config = null!;

    [SetUp]
    public void SetUp()
    {
        this.directory = Path.Combine(Path.GetTempPath(), "NoTunes-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.directory);
        this.config = Path.Combine(this.directory, "test.config");
    }

    [Test]
    public void Reads_The_Unc_Path_Without_Changing_Its_Backslashes()
    {
        File.WriteAllText(this.config, """
            <configuration><appSettings>
              <add key="MusicSourceFolder" value="\\192.168.0.150\music" />
            </appSettings></configuration>
            """);

        Assert.That(SourceSettings.ResolveFolder(null, this.config), Is.EqualTo(@"\\192.168.0.150\music"));
    }

    [Test]
    public void Explicit_Folder_Overrides_Configuration_Without_Reading_It()
    {
        Assert.That(SourceSettings.ResolveFolder(@"D:\Music", this.config), Is.EqualTo(@"D:\Music"));
    }

    [TestCase("<configuration><appSettings /></configuration>")]
    [TestCase("<configuration><appSettings><add key=\"MusicSourceFolder\" value=\" \" /></appSettings></configuration>")]
    [TestCase("<configuration>")]
    public void Rejects_Missing_Empty_Or_Malformed_Configuration(string xml)
    {
        File.WriteAllText(this.config, xml);
        Assert.Throws<InvalidDataException>(() => SourceSettings.ResolveFolder(null, this.config));
    }

    [Test]
    public void Rejects_An_Explicit_Empty_Folder_Instead_Of_Falling_Back_To_Configuration()
    {
        Assert.Throws<ArgumentException>(() => SourceSettings.ResolveFolder(" ", this.config));
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(this.directory, recursive: true);
    }
}
