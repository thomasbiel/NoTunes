using System.Security.Cryptography;
using NoTunes.Apple;
using StbImageSharp;
using StbImageWriteSharp;

namespace NoTunes.Music;

internal sealed record ArtworkImage(byte[] Bytes, uint CacheId = 0)
{
    internal const int MaximumBytes = 32 * 1024 * 1024;
    private const long MaximumPixels = 16 * 1024 * 1024;
    private const int JpegQuality = 90;
    private const int Opaque = 255;
    private const int RgbChannels = (int)StbImageSharp.ColorComponents.RedGreenBlue;
    private const int RgbaChannels = (int)StbImageSharp.ColorComponents.RedGreenBlueAlpha;
    private const int AlphaChannel = RgbaChannels - 1;
    private const int MinimumImageBytes = 4;
    private const uint SinglePictureCount = 1;

    private static ReadOnlySpan<byte> JpegStartMarker => [0xff, 0xd8];

    private static ReadOnlySpan<byte> JpegEndMarker => [0xff, 0xd9];

    public uint Checksum => checked((uint)this.Bytes.Length + SinglePictureCount);

    public string Sha256 => Convert.ToHexString(SHA256.HashData(this.Bytes));

    public static uint Allocate(ref uint highest)
    {
        if (highest >= ArtworkProtocol.MaximumCacheId) throw new IOException("The artwork cache namespace is exhausted.");

        return ++highest;
    }

    public static ArtworkImage Read(byte[] bytes)
    {
        if (bytes.Length is < MinimumImageBytes or > MaximumBytes)
            throw new InvalidDataException("Embedded cover exceeds its size limit.");

        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            var info = ImageInfo.FromStream(input);
            if (info is not { Width: > 0, Height: > 0 } dimensions
                || (long)dimensions.Width * dimensions.Height > MaximumPixels)
                throw new InvalidDataException("Invalid or oversized embedded cover dimensions.");

            var image = ImageResult.FromMemory(bytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            if (bytes.AsSpan().StartsWith(JpegStartMarker) && bytes.AsSpan().EndsWith(JpegEndMarker))
                return new ArtworkImage(bytes);

            var rgb = new byte[checked(image.Width * image.Height * RgbChannels)];
            for (var pixel = 0; pixel < image.Width * image.Height; pixel++)
            {
                var alpha = image.Data[pixel * RgbaChannels + AlphaChannel];
                for (var channel = 0; channel < RgbChannels; channel++)
                {
                    rgb[pixel * RgbChannels + channel] = (byte)((image.Data[pixel * RgbaChannels + channel] * alpha
                        + Opaque * (Opaque - alpha) + Opaque / 2) / Opaque);
                }
            }

            using var output = new MemoryStream();
            new ImageWriter().WriteJpg(
                rgb,
                image.Width,
                image.Height,
                StbImageWriteSharp.ColorComponents.RedGreenBlue,
                output,
                JpegQuality);
            if (output.Length > MaximumBytes) throw new InvalidDataException("Converted cover exceeds its size limit.");

            return new ArtworkImage(output.ToArray());
        }
        catch (Exception exception) when (exception is not IOException and not OutOfMemoryException)
        {
            throw new InvalidDataException("Cannot decode embedded artwork.", exception);
        }
    }
}
