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
        var objects = new List<object>();
        var top = Add(value, 0);
        var refWidth = Width((ulong)objects.Count);
        using var stream = new MemoryStream();
        stream.Write(Signature);
        var offsets = new List<ulong>();
        foreach (var item in objects)
        {
            offsets.Add((ulong)stream.Position);
            switch (item)
            {
                case bool flag:
                    stream.WriteByte(flag ? TrueMarker : FalseMarker);
                    break;
                case long number:
                    WriteInteger(stream, number);
                    break;
                case double number:
                    stream.WriteByte(DoubleMarker);
                    WriteNumber(stream, unchecked((ulong)BitConverter.DoubleToInt64Bits(number)), sizeof(ulong));
                    break;
                case DateTime date:
                    stream.WriteByte(DateMarker);
                    WriteNumber(stream, unchecked((ulong)BitConverter.DoubleToInt64Bits((date.ToUniversalTime() - Epoch).TotalSeconds)), sizeof(ulong));
                    break;
                case string text:
                    var ascii = text.All(c => c < AsciiCharacterLimit);
                    WriteLength(stream, (int)(ascii ? BinaryPlistObjectType.AsciiString : BinaryPlistObjectType.UnicodeString) << TypeShift, text.Length);
                    stream.Write((ascii ? Encoding.ASCII : Encoding.BigEndianUnicode).GetBytes(text));
                    break;
                case byte[] bytes:
                    WriteLength(stream, (int)BinaryPlistObjectType.Data << TypeShift, bytes.Length);
                    stream.Write(bytes);
                    break;
                case BinaryPlistReferences references:
                    WriteLength(stream, (int)(references.Dictionary ? BinaryPlistObjectType.Dictionary : BinaryPlistObjectType.Array) << TypeShift, references.Dictionary ? references.Indices.Length / 2 : references.Indices.Length);
                    foreach (var index in references.Indices)
                    {
                        WriteNumber(stream, (ulong)index, refWidth);
                    }

                    break;
                default:
                    throw new InvalidDataException("Unsupported binary property list object.");
            }

            if (stream.Length > Plist.MaxBytes) throw new InvalidDataException("Property list is too large.");
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
        if (stream.Length > Plist.MaxBytes) throw new InvalidDataException("Property list is too large.");

        return stream.ToArray();
        int Add(object item, int depth)
        {
            if (depth > Plist.MaxNestingDepth || objects.Count >= Plist.MaxObjectCount)
                throw new InvalidDataException("Property list is too complex.");

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

    private static int Width(ulong number)
    {
        return number <= byte.MaxValue ? 1 : number <= ushort.MaxValue ? 2 : number <= uint.MaxValue ? 4 : 8;
    }

    private static void WriteNumber(Stream stream, ulong number, int width)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, number);
        stream.Write(buffer[(sizeof(ulong) - width)..]);
    }
}
