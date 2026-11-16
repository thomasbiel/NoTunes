using System.Xml.Linq;
using NUnit.Framework;

namespace NoTunes.Tests;

public sealed class ProjectLayoutTests
{
    [Test]
    public void Project_Directories_Do_Not_Contain_Other_Projects()
    {
        var root = GetRepositoryRoot();
        var solution = XDocument.Load(Path.Combine(root, "NoTunes.slnx"));
        var projects = solution.Descendants("Project")
            .Select(project => Path.GetFullPath(Path.Combine(root, (string)project.Attribute("Path")!)))
            .ToArray();
        foreach (var project in projects)
        {
            var directory = Path.GetDirectoryName(project)!;
            foreach (var other in projects.Where(other => !StringComparer.Ordinal.Equals(project, other)))
            {
                var relative = Path.GetRelativePath(directory, other);
                Assert.That(relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal), Is.True,
                    $"Project '{project}' contains another project: '{other}'.");
            }
        }
    }

    [Test]
    public void Platform_Projects_Are_Grouped_In_Their_Solution_Folder()
    {
        var solution = XDocument.Load(Path.Combine(GetRepositoryRoot(), "NoTunes.slnx"));
        var folder = solution.Root!.Elements("Folder").SingleOrDefault(folder =>
            StringComparer.Ordinal.Equals((string?)folder.Attribute("Name"), "/Platform/"));
        Assert.That(folder, Is.Not.Null);
        var projects = folder!.Elements("Project")
            .Select(project => Path.GetFileNameWithoutExtension((string)project.Attribute("Path")!))
            .ToArray();
        Assert.That(projects, Is.EquivalentTo([
            "NoTunes.Platform.Abstractions",
            "NoTunes.Platform.Windows",
            "NoTunes.Platform.Linux"
        ]));
    }

    private static string GetRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NoTunes.slnx"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("The NoTunes solution directory was not found.");
    }
}
