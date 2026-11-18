using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NoTunes.Apple;

namespace NoTunes.Music;

internal static class DeviceState
{
    private const string TemporarySuffix = ".tmp";

    public static string DirectoryFor(string root, string device)
    {
        var key = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(DeviceUdid.Normalize(device).ToUpperInvariant())));

        var directory = Path.GetFullPath(Path.Combine(root, key));
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return directory;
    }

    public static void Save<T>(string path, T value, long maximumBytes = long.MaxValue)
    {
        var temporary = path + TemporarySuffix;
        using (var file = new FileStream(
            temporary,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        {
            JsonSerializer.Serialize(file, value);
            if (file.Length > maximumBytes) throw new InvalidDataException("The recovery state exceeds its size limit.");

            file.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    public static string Root(string? configured)
    {
        return configured ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppleClient.Name);
    }
}
