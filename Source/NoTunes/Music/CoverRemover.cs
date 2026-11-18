using System.Buffers;
using System.Security.Cryptography;
using NoTunes.Platform;

namespace NoTunes.Music;

public sealed class CoverRemover(ISourceFileSystem fileSystem)
{
    private const string Mp3Extension = ".mp3";
    private const string BackupPrefix = "NoTunes-cover-";
    private const string BackupExtension = ".bak";

    public async Task<CoverRemovalResult> RemoveAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        var file = new FileInfo(Path.GetFullPath(path));
        if (!StringComparer.OrdinalIgnoreCase.Equals(file.Extension, Mp3Extension))
        {
            throw new InvalidDataException("Cover removal supports MP3 files only.");
        }

        using var session = fileSystem.OpenSource(file.Directory!, cancellationToken);
        if (session is not ISourceEditSession editor)
        {
            throw new NotSupportedException("The source provider does not support guarded metadata editing.");
        }

        await using var stream = editor.OpenEdit(file);
        var modified = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
        using var tags = TagLib.File.Create(new SourceTagFile(stream, file.FullName, writable: true), TagLib.ReadStyle.Average);
        var covers = CountPictures(tags.Tag);
        if (covers == 0) return new CoverRemovalResult(file.FullName, 0);

        var audioStart = tags.InvariantStartPosition;
        var audioEnd = tags.InvariantEndPosition;
        var originalLength = stream.Length;
        var backupPath = Path.Combine(Path.GetTempPath(), BackupPrefix + Guid.NewGuid().ToString("N") + BackupExtension);
        var backup = new FileStream(
            backupPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            SourceFileDefaults.ReadBufferBytes,
            FileOptions.Asynchronous);

        var keepBackup = false;
        var edited = false;
        try
        {
            stream.Position = 0;
            await stream.CopyToAsync(backup, cancellationToken);
            backup.Flush(flushToDisk: true);
            if (backup.Length != originalLength || stream.Length != originalLength || File.GetLastWriteTimeUtc(stream.SafeFileHandle) != modified)
            {
                throw new IOException("The source changed while its cover-removal backup was created.");
            }

            var audioHash = await HashAudioAsync(
                backup,
                audioStart,
                audioEnd,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            tags.Tag.Pictures = [];
            stream.Position = 0;
            edited = true;
            tags.Save();
            stream.Flush(flushToDisk: true);
            cancellationToken.ThrowIfCancellationRequested();

            using var verified = TagLib.File.Create(new SourceTagFile(stream, file.FullName), TagLib.ReadStyle.Average);
            if (CountPictures(verified.Tag) != 0) throw new IOException("Embedded covers remain after saving the MP3.");

            var verifiedHash = await HashAudioAsync(
                stream,
                verified.InvariantStartPosition,
                verified.InvariantEndPosition,
                cancellationToken);

            if (verified.InvariantEndPosition - verified.InvariantStartPosition != audioEnd - audioStart || !StringComparer.Ordinal.Equals(verifiedHash, audioHash))
            {
                throw new IOException("The MP3 audio changed while removing its embedded covers.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var updated = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(stream.SafeFileHandle, updated == modified ? updated.AddTicks(1) : updated);
            stream.Flush(flushToDisk: true);
            return new CoverRemovalResult(file.FullName, covers);
        }
        catch (Exception failure)
        {
            if (edited)
            {
                try
                {
                    backup.Position = 0;
                    stream.Position = 0;
                    await backup.CopyToAsync(stream, CancellationToken.None);
                    stream.SetLength(backup.Length);
                    File.SetLastWriteTimeUtc(stream.SafeFileHandle, modified);
                    stream.Flush(flushToDisk: true);
                }
                catch (Exception restoreFailure)
                {
                    keepBackup = true;
                    throw new IOException($"Cover removal failed and the original could not be restored. "
                        + $"The original backup is retained at '{backupPath}'.", new AggregateException(failure, restoreFailure));
                }
            }

            throw;
        }
        finally
        {
            await backup.DisposeAsync();
            if (!keepBackup) File.Delete(backupPath);
        }
    }

    private static int CountPictures(TagLib.Tag tag)
    {
        return tag is TagLib.CombinedTag combined ? combined.Tags.Sum(CountPictures) : tag.Pictures.Length;
    }

    private static async Task<string> HashAudioAsync(
        FileStream stream,
        long start,
        long end,
        CancellationToken cancellationToken)
    {
        if (start < 0 || end <= start || end > stream.Length) throw new InvalidDataException("The MP3 audio boundaries could not be verified.");

        stream.Position = start;
        var remaining = end - start;
        var buffer = ArrayPool<byte>.Shared.Rent(SourceFileDefaults.ReadBufferBytes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            while (remaining != 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), cancellationToken);
                if (read == 0) throw new EndOfStreamException("The MP3 audio ended before its verified boundary.");

                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
