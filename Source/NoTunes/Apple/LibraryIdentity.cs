using System.Buffers.Binary;
using System.Security.Cryptography;
using static NoTunes.Apple.LibraryPreferencesFormat;

namespace NoTunes.Apple;

internal sealed record LibraryIdentity(string LibraryId, string Binding)
{
    public static LibraryIdentity Parse(byte[] content)
    {
        if (!content.AsSpan().StartsWith(Magic))
        {
            var plist = Plist.Parse(content).AsDict();
            content = plist.GetValueOrDefault(PreferencesKey) as byte[]
                ?? throw new InvalidDataException("Missing iPodPrefs library identity.");
        }

        if (content.Length != Length || !content.AsSpan().StartsWith(Magic)
            || BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan(MajorVersionOffset)) != MajorVersion
            || BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan(MinorVersionOffset)) != MinorVersion
            || content[ConfiguredOffset] != Configured)
        {
            throw new InvalidDataException("Unsupported or unconfigured iTunes preferences.");
        }

        var catalog = BinaryPrimitives.ReadUInt64LittleEndian(content.AsSpan(CatalogueIdentityOffset));
        var user = BinaryPrimitives.ReadUInt64LittleEndian(content.AsSpan(UserIdentityOffset));
        if (catalog == 0 || user == 0 || catalog == user)
        {
            throw new InvalidDataException("Invalid saved music library identity.");
        }

        return new LibraryIdentity((catalog ^ user).ToString("X16"), Convert.ToHexString(SHA256.HashData(content.AsSpan(CatalogueIdentityOffset, 2 * sizeof(ulong)))));
    }

    public static async Task<LibraryIdentity> ReadAsync(AfcClient afc, CancellationToken cancellationToken)
    {
        LibraryIdentity? result = null;
        foreach (var path in new[] { SyncPaths.Preferences, SyncPaths.PreferencesPlist })
        {
            if (await afc.GetFileInfoAsync(path, cancellationToken) is null)
                continue;
            var identity = Parse(await afc.ReadFileAsync(path, SyncProtocol.MaxPreferenceBytes, cancellationToken));
            if (result is not null && result != identity)
            {
                throw new InvalidDataException("The saved library identities disagree.");
            }

            result = identity;
        }

        return result ?? throw new IOException("No configured music library identity found. "
            + "Run 'notunes init' to prepare a new, unsynced iPhone.");
    }
}
