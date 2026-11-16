using System.Text.Json;
using NUnit.Framework;

namespace NoTunes.Tests;

public sealed class DependencyLicenseTests
{
    private const string NoticesFileName = "THIRD-PARTY-NOTICES.md";
    private const string LicensesDirectoryName = "Licenses";
    private const string ApplicationDependenciesFileName = "NoTunes.deps.json";

    [TestCase("BouncyCastle-LICENSE.md", "The Legion of the Bouncy Castle Inc.")]
    [TestCase("CommandLineParser-LICENSE.md", "Giacomo Stelluti Scala & Contributors")]
    [TestCase("Microsoft-LICENSE.txt", ".NET Foundation and Contributors")]
    [TestCase("Microsoft-THIRD-PARTY-NOTICES.txt", "License notice for ASP.NET")]
    [TestCase("SQLite-LICENSE.txt", "SQLite is Public Domain")]
    public void Ships_Required_Dependency_License_Files(string fileName, string expectedNotice)
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, LicensesDirectoryName, fileName);
        Assert.That(File.Exists(path), Is.True, $"Missing distributed dependency notice: {fileName}");
        var content = File.ReadAllText(path);
        Assert.That(content, Does.Contain(expectedNotice));
        if (fileName.EndsWith("LICENSE.md", StringComparison.Ordinal)
            || StringComparer.Ordinal.Equals(fileName, "Microsoft-LICENSE.txt"))
        {
            Assert.That(content, Does.Contain("Permission is hereby granted, free of charge"));
            Assert.That(content, Does.Contain("SOFTWARE."));
        }
    }

    [Test]
    public void Documents_Every_Shipped_Runtime_Package_And_Version()
    {
        var output = TestContext.CurrentContext.TestDirectory;
        var notices = File.ReadAllText(Path.Combine(output, NoticesFileName));
        using var dependencies = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(output, ApplicationDependenciesFileName)));
        foreach (var library in dependencies.RootElement.GetProperty("libraries").EnumerateObject())
        {
            if (StringComparer.Ordinal.Equals(library.Value.GetProperty("type").GetString(), "package"))
            {
                Assert.That(notices, Does.Contain(library.Name), $"Missing runtime package attribution: {library.Name}");
            }
        }
    }
}
