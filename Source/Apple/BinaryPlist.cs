using System.Buffers.Binary;
using System.Text;

namespace NoTunes.Apple;

// Apple's documented bplist00 container. Supports the types used by lockdown and ATC.
public static class BinaryPlist
{
    private static readonly DateTime Epoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public static object Read(byte[] data) => new Reader(data).Read();

    public static byte[] Write(object value)
    {
        var objects = new List<object>();
        var top = Add(value, 0);
        var refWidth = Width((ulong)objects.Count);
        using var stream = new MemoryStream();
        stream.Write("bplist00"u8);
        var offsets = new List<ulong>();
        foreach (var item in objects)
        {
            offsets.Add((ulong)stream.Position);
            switch (item)
            {
                case bool flag: stream.WriteByte(flag ? (byte)9 : (byte)8); break;
                case long number:
                    var width = number < 0 ? 8 : Width((ulong)number);
                    stream.WriteByte((byte)(0x10 | (width == 1 ? 0 : width == 2 ? 1 : width == 4 ? 2 : 3)));
                    WriteNumber(stream, unchecked((ulong)number), width);
                    break;
                case double number:
                    stream.WriteByte(0x23);
                    WriteNumber(stream, unchecked((ulong)BitConverter.DoubleToInt64Bits(number)), 8);
                    break;
                case DateTime date:
                    stream.WriteByte(0x33);
                    WriteNumber(stream, unchecked((ulong)BitConverter.DoubleToInt64Bits((date.ToUniversalTime() - Epoch).TotalSeconds)), 8);
                    break;
                case string text:
                    var ascii = text.All(c => c < 128);
                    WriteLength(stream, ascii ? 0x50 : 0x60, text.Length);
                    stream.Write((ascii ? Encoding.ASCII : Encoding.BigEndianUnicode).GetBytes(text));
                    break;
                case byte[] bytes:
                    WriteLength(stream, 0x40, bytes.Length);
                    stream.Write(bytes);
                    break;
                case References references:
                    WriteLength(stream, references.Dictionary ? 0xD0 : 0xA0,
                        references.Dictionary ? references.Indices.Length / 2 : references.Indices.Length);
                    foreach (var index in references.Indices) WriteNumber(stream, (ulong)index, refWidth);
                    break;
                default: throw new InvalidDataException("Unsupported binary property list object.");
            }
            if (stream.Length > Plist.MaxBytes) throw new InvalidDataException("Property list is too large.");
        }
        var table = (ulong)stream.Position;
        var offsetWidth = Width(table);
        foreach (var offset in offsets) WriteNumber(stream, offset, offsetWidth);
        stream.Write(new byte[6]);
        stream.WriteByte((byte)offsetWidth);
        stream.WriteByte((byte)refWidth);
        WriteNumber(stream, (ulong)objects.Count, 8);
        WriteNumber(stream, (ulong)top, 8);
        WriteNumber(stream, table, 8);
        if (stream.Length > Plist.MaxBytes) throw new InvalidDataException("Property list is too large.");
        return stream.ToArray();

        int Add(object item, int depth)
        {
            if (depth > 64 || objects.Count >= 100_000) throw new InvalidDataException("Property list is too complex.");
            var index = objects.Count;
            objects.Add(false);
            objects[index] = item switch
            {
                Dictionary<string, object> dictionary => new References(true,
                    dictionary.Keys.Select(k => Add(k, depth + 1))
                        .Concat(dictionary.Values.Select(v => Add(v, depth + 1))).ToArray()),
                byte[] bytes => bytes,
                IEnumerable<object> array => new References(false, array.Select(v => Add(v, depth + 1)).ToArray()),
                int number => (long)number,
                long or double or bool or string or DateTime => item,
                _ => throw new InvalidDataException("Unsupported property list type.")
            };
            return index;
        }
    }

    private sealed record References(bool Dictionary, int[] Indices);
    private static void WriteLength(Stream stream, int marker, int length)
    {
        if (length < 15)
        {
            stream.WriteByte((byte)(marker | length));
            return;
        }
        stream.WriteByte((byte)(marker | 15));
        var width = Width((ulong)length);
        stream.WriteByte((byte)(0x10 | (width == 1 ? 0 : width == 2 ? 1 : width == 4 ? 2 : 3)));
        WriteNumber(stream, (ulong)length, width);
    }
    private static int Width(ulong number) => number <= byte.MaxValue ? 1 : number <= ushort.MaxValue ? 2
        : number <= uint.MaxValue ? 4 : 8;
    private static void WriteNumber(Stream stream, ulong number, int width)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, number);
        stream.Write(buffer[(8 - width)..]);
    }
    private sealed class Reader(byte[] data)
    {
        private int offsetWidth;
        private int refWidth;
        private int count;
        private int table;
        private int visited;
        private readonly HashSet<int> active = [];

        public object Read()
        {
            if (data.Length < 40 || data.Length > Plist.MaxBytes || !data.AsSpan().StartsWith("bplist00"u8))
                throw new InvalidDataException("Invalid binary property list.");
            var trailer = data.Length - 32;
            offsetWidth = data[trailer + 6];
            refWidth = data[trailer + 7];
            count = Int(Number(trailer + 8, 8));
            var top = Int(Number(trailer + 16, 8));
            table = Int(Number(trailer + 24, 8));
            if (offsetWidth is not (1 or 2 or 4 or 8) || refWidth is not (1 or 2 or 4 or 8)
                || count < 1 || count > 100_000 || top >= count || table < 8
                || (long)table + (long)count * offsetWidth > trailer)
                throw new InvalidDataException("Invalid binary property list trailer.");
            return Object(top, 0);
        }

        private object Object(int index, int depth)
        {
            // Bound visits too: repeated references to shared nested arrays can expand exponentially.
            if (depth > 64 || ++visited > 100_000 || index < 0 || index >= count || !active.Add(index))
                throw new InvalidDataException("Cyclic or overly complex binary property list.");
            try
            {
                var position = Int(Number(table + index * offsetWidth, offsetWidth));
                if (position < 8 || position >= table) throw new InvalidDataException("Invalid property list object offset.");
                var marker = data[position++];
                var type = marker >> 4;
                var info = marker & 15;
                switch (type)
                {
                    case 0 when info == 8: return false;
                    case 0 when info == 9: return true;
                    case 1:
                        if (info > 3) throw new InvalidDataException("Property list integer exceeds 64 bits.");
                        Slice(position, 1 << info);
                        return unchecked((long)Number(position, 1 << info));
                    case 2 when info is 2 or 3:
                        Slice(position, 1 << info);
                        return info == 2 ? (double)BitConverter.Int32BitsToSingle(unchecked((int)Number(position, 4)))
                            : BitConverter.Int64BitsToDouble(unchecked((long)Number(position, 8)));
                    case 3 when info == 3:
                        Slice(position, 8);
                        try { return Epoch.AddSeconds(BitConverter.Int64BitsToDouble(unchecked((long)Number(position, 8)))); }
                        catch (ArgumentException e) { throw new InvalidDataException("Invalid property list date.", e); }
                    case 4:
                        var byteCount = Length(info, ref position);
                        return Slice(position, byteCount).ToArray();
                    case 5:
                    case 6:
                        var length = Length(info, ref position);
                        var bytes = Slice(position, checked(length * (type == 6 ? 2 : 1)));
                        return (type == 6 ? Encoding.BigEndianUnicode : Encoding.ASCII).GetString(bytes);
                    case 10:
                    case 13:
                        var entries = Length(info, ref position);
                        if (entries > 100_000) throw new InvalidDataException("Too many property list entries.");
                        Slice(position, checked(entries * refWidth * (type == 13 ? 2 : 1)));
                        if (type == 10)
                        {
                            var array = new object[entries];
                            for (var i = 0; i < entries; i++) array[i] = Object(Reference(position + i * refWidth), depth + 1);
                            return array;
                        }
                        var dictionary = new Dictionary<string, object>(StringComparer.Ordinal);
                        for (var i = 0; i < entries; i++)
                        {
                            var key = Object(Reference(position + i * refWidth), depth + 1) as string
                                ?? throw new InvalidDataException("Property list key is not a string.");
                            var value = Object(Reference(position + (entries + i) * refWidth), depth + 1);
                            if (!dictionary.TryAdd(key, value)) throw new InvalidDataException("Duplicate property list key.");
                        }
                        return dictionary;
                    default: throw new InvalidDataException($"Unsupported binary property list type: 0x{marker:X2}");
                }
            }
            catch (OverflowException e) { throw new InvalidDataException("Property list size overflow.", e); }
            finally { active.Remove(index); }
        }

        private int Reference(int position) => Int(Number(position, refWidth));
        private int Length(int info, ref int position)
        {
            if (info != 15) return info;
            Slice(position, 1);
            var marker = data[position++];
            if ((marker >> 4) != 1 || (marker & 15) > 3) throw new InvalidDataException("Invalid property list length.");
            var width = 1 << (marker & 15);
            Slice(position, width);
            var length = Int(Number(position, width));
            position += width;
            return length;
        }
        private ReadOnlySpan<byte> Slice(int start, int length)
        {
            if (start < 0 || length < 0 || (long)start + length > table)
                throw new InvalidDataException("Truncated binary property list.");
            return data.AsSpan(start, length);
        }
        private ulong Number(int position, int width)
        {
            if (position < 0 || width is < 1 or > 8 || (long)position + width > data.Length)
                throw new InvalidDataException("Truncated binary property list.");
            ulong value = 0;
            for (var i = 0; i < width; i++) value = (value << 8) | data[position + i];
            return value;
        }
        private static int Int(ulong value) => value <= int.MaxValue ? (int)value
            : throw new InvalidDataException("Binary property list is too large.");
    }
}
