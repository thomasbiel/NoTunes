using System.Buffers.Binary;
using System.Text;
using static NoTunes.Apple.BinaryPlistFormat;

namespace NoTunes.Apple;

// Apple's documented bplist00 container. Supports the types used by lockdown and ATC.
public static class BinaryPlist
{
    public static object Read(byte[] data) => new BinaryPlistReader(data).Read();

    public static byte[] Write(object value)
    {
        return WriteWithinLimit(value, Plist.MaxBytes) ?? throw new InvalidDataException("Property list is too large.");
    }

    internal static byte[]? WriteWithinLimit(object value, int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var objects = new List<object>();
        var top = Add(value, 0);
        var refWidth = Width((ulong)objects.Count);
        using var stream = new MemoryStream();
        stream.Write(Signature);
        var offsets = new List<ulong>();
        foreach (var item in objects)
        {
            offsets.Add((ulong)stream.Position);
            WriteObject(stream, item, refWidth);

            if (stream.Length > maxBytes) return null;
        }

        var table = (ulong)stream.Position;
        var offsetWidth = Width(table);
        foreach (var offset in offsets)
        {
            WriteNumber(stream, offset, offsetWidth);
        }

        stream.Write(new byte[TrailerReservedBytes]);
        stream.WriteByte((byte)offsetWidth);
        stream.WriteByte((byte)refWidth);
        WriteNumber(stream, (ulong)objects.Count, sizeof(ulong));
        WriteNumber(stream, (ulong)top, sizeof(ulong));
        WriteNumber(stream, table, sizeof(ulong));
        if (stream.Length > maxBytes) return null;

        return stream.ToArray();
        int Add(object item, int depth)
        {
            if (depth > Plist.MaxNestingDepth || objects.Count >= Plist.MaxObjectCount)
            {
                throw new InvalidDataException("Property list is too complex.");
            }

            var index = objects.Count;
            objects.Add(false);
            objects[index] = item switch
            {
                Dictionary<string, object> dictionary => new BinaryPlistReferences(true,
                    [.. dictionary.Keys.Select(k => Add(k, depth + 1)), .. dictionary.Values.Select(v => Add(v, depth + 1))]),
                byte[] bytes => bytes,
                IEnumerable<object> array => new BinaryPlistReferences(false, [.. array.Select(v => Add(v, depth + 1))]),
                int number => (long)number,
                long or double or bool or string or DateTime => item,
                _ => throw new InvalidDataException("Unsupported property list type.")
            };

            return index;
        }
    }

    private static void WriteObject(Stream stream, object item, int referenceWidth)
    {
        switch (item)
        {
            case bool flag:
                stream.WriteByte(flag ? TrueMarker : FalseMarker);
                break;
            case long number:
                WriteInteger(stream, number);
                break;
            case double number:
                WriteReal(stream, DoubleMarker, number);
                break;
            case DateTime date:
                WriteReal(stream, DateMarker, (date.ToUniversalTime() - Epoch).TotalSeconds);
                break;
            case string text:
                WriteString(stream, text);
                break;
            case byte[] bytes:
                WriteLength(stream, (int)BinaryPlistObjectType.Data << TypeShift, bytes.Length);
                stream.Write(bytes);
                break;
            case BinaryPlistReferences references:
                WriteReferences(stream, references, referenceWidth);
                break;
            default:
                throw new InvalidDataException("Unsupported binary property list object.");
        }
    }

    private static void WriteReal(Stream stream, byte marker, double value)
    {
        stream.WriteByte(marker);
        WriteNumber(stream, unchecked((ulong)BitConverter.DoubleToInt64Bits(value)), sizeof(ulong));
    }

    private static void WriteString(Stream stream, string text)
    {
        var ascii = IsAscii(text);
        var kind = ascii ? BinaryPlistObjectType.AsciiString : BinaryPlistObjectType.UnicodeString;
        WriteLength(stream, (int)kind << TypeShift, text.Length);
        stream.Write((ascii ? Encoding.ASCII : Encoding.BigEndianUnicode).GetBytes(text));
    }

    private static void WriteReferences(Stream stream, BinaryPlistReferences references, int width)
    {
        var kind = references.Dictionary ? BinaryPlistObjectType.Dictionary : BinaryPlistObjectType.Array;
        var count = references.Dictionary ? references.Indices.Length / 2 : references.Indices.Length;
        WriteLength(stream, (int)kind << TypeShift, count);
        foreach (var index in references.Indices)
        {
            WriteNumber(stream, (ulong)index, width);
        }
    }

    private static void WriteLength(Stream stream, int marker, int length)
    {
        if (length < ExtendedLengthMarker)
        {
            stream.WriteByte((byte)(marker | length));
            return;
        }

        stream.WriteByte((byte)(marker | ExtendedLengthMarker));
        WriteInteger(stream, length);
    }

    private static void WriteInteger(Stream stream, long number)
    {
        var width = number < 0 ? sizeof(long) : Width((ulong)number);
        stream.WriteByte((byte)(((int)BinaryPlistObjectType.Integer << TypeShift) | (width == 1 ? 0 : width == 2 ? 1 : width == 4 ? 2 : 3)));
        WriteNumber(stream, unchecked((ulong)number), width);
    }

    private static void WriteNumber(Stream stream, ulong number, int width)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, number);
        stream.Write(buffer[(sizeof(ulong) - width)..]);
    }
}
