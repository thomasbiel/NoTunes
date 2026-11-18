using System.Security.Cryptography;

namespace NoTunes.Music;

internal sealed record ArtworkFile(string Path, int Length, string Sha256)
{
    public static ArtworkFile Save(string path, byte[] bytes)
    {
        using (var output = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None))
        {
            output.Write(bytes);
        }

        var file = new ArtworkFile(path, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
        file.Validate();
        return file;
    }

    public byte[] ReadBytes()
    {
        using var input = File.OpenRead(this.Path);
        this.RequireLength(input);
        var bytes = new byte[this.Length];
        input.ReadExactly(bytes);
        this.RequireLength(input);
        this.RequireHash(SHA256.HashData(bytes));
        return bytes;
    }

    public void Validate()
    {
        using var input = File.OpenRead(this.Path);
        this.RequireLength(input);
        var hash = SHA256.HashData(input);
        this.RequireLength(input);
        this.RequireHash(hash);
    }

    private void RequireLength(Stream input)
    {
        if (this.Length is < 0 or > ArtworkImage.MaximumBytes || input.Length != this.Length)
        {
            throw new IOException("Prepared artwork size differs from its verified record.");
        }
    }

    private void RequireHash(byte[] hash)
    {
        if (!StringComparer.Ordinal.Equals(Convert.ToHexString(hash), this.Sha256))
        {
            throw new IOException("Prepared artwork bytes differ from their verified record.");
        }
    }
}
