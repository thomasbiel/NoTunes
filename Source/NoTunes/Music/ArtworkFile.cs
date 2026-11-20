using System.Text.Json.Serialization;

namespace NoTunes.Music;

internal sealed record ArtworkFile(
    string Path,
    int Length,
    [property: JsonPropertyName(ContentHash.AlgorithmName)] string Hash)
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

        var file = new ArtworkFile(path, bytes.Length, ContentHash.Compute(bytes));
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
        this.RequireHash(ContentHash.Compute(bytes));
        return bytes;
    }

    public void Validate()
    {
        using var input = File.OpenRead(this.Path);
        this.RequireLength(input);
        var hash = ContentHash.Compute(input);
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

    private void RequireHash(string hash)
    {
        if (!StringComparer.Ordinal.Equals(hash, this.Hash))
        {
            throw new IOException("Prepared artwork bytes differ from their verified record.");
        }
    }
}
