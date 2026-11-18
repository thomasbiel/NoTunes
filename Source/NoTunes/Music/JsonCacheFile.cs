using System.Text.Json;

namespace NoTunes.Music;

internal static class JsonCacheFile
{
    private const string TemporaryExtension = ".tmp";

    public static T? Read<T>(
        string path,
        Func<Stream, T> read,
        TextWriter error,
        string name,
        long maximumBytes = long.MaxValue) where T : class
    {
        try
        {
            using var file = File.OpenRead(path);
            RequireSize(file, maximumBytes, name);
            return read(file);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            error.WriteLine($"{name} could not be read; files will be hashed again: {exception.Message}");
            return null;
        }
    }

    public static void Write<T>(
        string path,
        T value,
        TextWriter error,
        string name,
        long maximumBytes = long.MaxValue)
    {
        var temporary = path + Guid.NewGuid().ToString("N") + TemporaryExtension;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var file = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                JsonSerializer.Serialize(file, value);
                RequireSize(file, maximumBytes, name);
                file.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            error.WriteLine($"{name} could not be saved: {exception.Message}");
        }
        finally
        {
            RemoveTemporary(temporary, error, name);
        }
    }

    private static void RequireSize(Stream file, long maximumBytes, string name)
    {
        if (file.Length > maximumBytes) throw new InvalidDataException($"{name} exceeds its size limit.");
    }

    private static void RemoveTemporary(string path, TextWriter error, string name)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var description = char.ToLowerInvariant(name[0]) + name[1..];
            error.WriteLine($"Temporary {description} could not be removed: {exception.Message}");
        }
    }
}
