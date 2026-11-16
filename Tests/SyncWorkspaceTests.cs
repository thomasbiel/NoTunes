using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

public sealed class SyncWorkspaceTests
{
    [Test]
    [Platform("Linux")]
    public void Restricts_Workspace_Access_To_The_Current_User()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Unix permissions are checked on Linux.");
            return;
        }

        using var workspace = new SyncWorkspace();
        var file = Path.Combine(workspace.Root, "private-copy");
        File.WriteAllText(file, "Private catalogue and music data");
        var privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Assert.That(File.GetUnixFileMode(workspace.Root), Is.EqualTo(privateMode));
        Assert.That(File.ReadAllText(file), Is.EqualTo("Private catalogue and music data"));
    }
}
