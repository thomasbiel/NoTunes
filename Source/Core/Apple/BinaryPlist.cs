using System.Buffers.Binary;
using System.Text;

namespace NoTunes.Apple;

// Apple's documented bplist00 container. Supports the types used by lockdown and ATC.
public static class BinaryPlist
{
    private const int TrailerReservedBytes = 6;
    private const int OffsetWidthOffset = TrailerReservedBytes;
    private const int ReferenceWidthOffset = OffsetWidthOffset + 1;
    private const int ObjectCountOffset = ReferenceWidthOffset + 1;
    private const int TopObjectOffset = ObjectCountOffset + sizeof(ulong);
    private const int OffsetTableOffset = TopObjectOffset + sizeof(ulong);
    private const int TrailerBytes = OffsetTableOffset + sizeof(ulong);
    private const int TypeShift = 4;
    private const int ExtendedLengthMarker = 15;
    private const int SingleWidthExponent = 2;
    private const int DoubleWidthExponent = 3;
    private const int AsciiCharacterLimit = 128;
    private const int BitsPerByte = 8;
    private const byte FalseMarker = 8;
    private const byte TrueMarker = 9;
    private const byte DoubleMarker = ((int)ObjectType.Real << TypeShift) | DoubleWidthExponent;
    private const byte DateMarker = ((int)ObjectType.Date << TypeShift) | DoubleWidthExponent;
    internal static ReadOnlySpan<byte> Signature => "bplist00"u8;

    private enum ObjectType
    {
        Simple = 0,
        Integer = 1,
        Real = 2,
        Date = 3,
        Data = 4,
        AsciiString = 5,
        UnicodeString = 6,
        Array = 10,
        Dictionary = 13
    }

    private static readonly DateTime Epoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public static object Read(byte[] data) => new Reader(data).Read();

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
                case bool flag: stream.WriteByte(flag ? TrueMarker : FalseMarker); break;
                case long number:
                    var width = number < 0 ? sizeof(long) : Width((ulong)number);
                    stream.WriteByte((byte)(((int)ObjectType.Integer << TypeShift) | (width == 1 ? 0 : width == 2 ? 1 : width == 4 ? 2 : 3)));
                    WriteNumber(stream, unchecked((ulong)number), width);
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
                    WriteLength(stream, (int)(ascii ? ObjectType.AsciiString : ObjectType.UnicodeString) << TypeShift, text.Length);
                    stream.Write((ascii ? Encoding.ASCII : Encoding.BigEndianUnicode).GetBytes(text));
                    break;
                case byte[] bytes:
                    WriteLength(stream, (int)ObjectType.Data << TypeShift, bytes.Length);
                    stream.Write(bytes);
                    break;
                case References references:
                    WriteLength(stream, (int)(references.Dictionary ? ObjectType.Dictionary : ObjectType.Array) << TypeShift,
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
            if (depth > Plist.MaxNestingDepth || objects.Count >= Plist.MaxObjectCount) throw new InvalidDataException("Property list is too complex.");
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
        if (length < ExtendedLengthMarker)
        {
            stream.WriteByte((byte)(marker | length));
            return;
        }
        stream.WriteByte((byte)(marker | ExtendedLengthMarker));
        var width = Width((ulong)length);
        stream.WriteByte((byte)(((int)ObjectType.Integer << TypeShift) | (width == 1 ? 0 : width == 2 ? 1 : width == 4 ? 2 : 3)));
        WriteNumber(stream, (ulong)length, width);
    }
    private static int Width(ulong number) => number <= byte.MaxValue ? 1 : number <= ushort.MaxValue ? 2
        : number <= uint.MaxValue ? 4 : 8;
    private static void WriteNumber(Stream stream, ulong number, int width)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, number);
        stream.Write(buffer[(sizeof(ulong) - width)..]);
    }
    private sealed class Reader(byte[] data)
    {
        private const long MaxDecodedBytes = 4L * Plist.MaxBytes;
        private const int EstimatedObjectBytes = 128;
        private const int EstimatedDictionaryEntryBytes = 64;
        private int offsetWidth;
        private int refWidth;
        private int count;
        private int table;
        private int visited;
        private long decodedBytes;
        private readonly HashSet<int> active = [];
        // Include depth so sharing cannot bypass the nesting limit on a deeper path.
        private readonly Dictionary<(int Index, int Depth), object> cache = [];

        public object Read()
        {
            if (data.Length < Signature.Length + TrailerBytes || data.Length > Plist.MaxBytes || !data.AsSpan().StartsWith(Signature))
                throw new InvalidDataException("Invalid binary property list.");
            var trailer = data.Length - TrailerBytes;
            offsetWidth = data[trailer + OffsetWidthOffset];
            refWidth = data[trailer + ReferenceWidthOffset];
            count = Int(Number(trailer + ObjectCountOffset, sizeof(ulong)));
            var top = Int(Number(trailer + TopObjectOffset, sizeof(ulong)));
            table = Int(Number(trailer + OffsetTableOffset, sizeof(ulong)));
            if (offsetWidth is not (1 or 2 or 4 or 8) || refWidth is not (1 or 2 or 4 or 8)
                || count < 1 || count > Plist.MaxObjectCount || top >= count || table < Signature.Length
                || (long)table + (long)count * offsetWidth > trailer)
                throw new InvalidDataException("Invalid binary property list trailer.");
            return Object(top, 0);
        }

        private object Object(int index, int depth)
        {
            // Bound reference traversal even when cached objects are shared.
            if (depth > Plist.MaxNestingDepth || ++visited > Plist.MaxObjectCount || index < 0 || index >= count || !active.Add(index))
                throw new InvalidDataException("Cyclic or overly complex binary property list.");
            try
            {
                if (cache.TryGetValue((index, depth), out var cached)) return cached;
                ReserveDecodedBytes(EstimatedObjectBytes);
                var value = ReadObject(index, depth);
                cache.Add((index, depth), value);
                return value;
            }
            catch (OverflowException e) { throw new InvalidDataException("Property list size overflow.", e); }
            finally { active.Remove(index); }
        }

        private object ReadObject(int index, int depth)
        {
            var position = Int(Number(table + index * offsetWidth, offsetWidth));
            if (position < Signature.Length || position >= table) throw new InvalidDataException("Invalid property list object offset.");
            var marker = data[position++];
            var type = (ObjectType)(marker >> TypeShift);
            var info = marker & ExtendedLengthMarker;
            switch (type)
            {
                case ObjectType.Simple when info == FalseMarker: return false;
                case ObjectType.Simple when info == TrueMarker: return true;
                case ObjectType.Integer:
                    if (info > DoubleWidthExponent) throw new InvalidDataException("Property list integer exceeds 64 bits.");
                    Slice(position, 1 << info);
                    return unchecked((long)Number(position, 1 << info));
                case ObjectType.Real when info is SingleWidthExponent or DoubleWidthExponent:
                    Slice(position, 1 << info);
                    return info == SingleWidthExponent ? (double)BitConverter.Int32BitsToSingle(unchecked((int)Number(position, sizeof(float))))
                        : BitConverter.Int64BitsToDouble(unchecked((long)Number(position, sizeof(double))));
                case ObjectType.Date when info == DoubleWidthExponent:
                    Slice(position, sizeof(ulong));
                    try { return Epoch.AddSeconds(BitConverter.Int64BitsToDouble(unchecked((long)Number(position, sizeof(double))))); }
                    catch (ArgumentException e) { throw new InvalidDataException("Invalid property list date.", e); }
                case ObjectType.Data:
                    var byteCount = Length(info, ref position);
                    var dataBytes = Slice(position, byteCount);
                    ReserveDecodedBytes(byteCount);
                    return dataBytes.ToArray();
                case ObjectType.AsciiString:
                case ObjectType.UnicodeString:
                    var length = Length(info, ref position);
                    var bytes = Slice(position, checked(length * (type == ObjectType.UnicodeString ? sizeof(char) : sizeof(byte))));
                    ReserveDecodedBytes((long)length * sizeof(char));
                    return (type == ObjectType.UnicodeString ? Encoding.BigEndianUnicode : Encoding.ASCII).GetString(bytes);
                case ObjectType.Array:
                case ObjectType.Dictionary:
                    var entries = Length(info, ref position);
                    if (entries > Plist.MaxObjectCount) throw new InvalidDataException("Too many property list entries.");
                    Slice(position, checked(entries * refWidth * (type == ObjectType.Dictionary ? 2 : 1)));
                    ReserveDecodedBytes((long)entries * (type == ObjectType.Array ? IntPtr.Size : EstimatedDictionaryEntryBytes));
                    if (type == ObjectType.Array)
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

        private void ReserveDecodedBytes(long bytes)
        {
            if (bytes > MaxDecodedBytes - decodedBytes)
                throw new InvalidDataException("Decoded binary property list is too large.");
            decodedBytes += bytes;
        }

        private int Reference(int position) => Int(Number(position, refWidth));
        private int Length(int info, ref int position)
        {
            if (info != ExtendedLengthMarker) return info;
            Slice(position, 1);
            var marker = data[position++];
            if ((ObjectType)(marker >> TypeShift) != ObjectType.Integer || (marker & ExtendedLengthMarker) > DoubleWidthExponent) throw new InvalidDataException("Invalid property list length.");
            var width = 1 << (marker & ExtendedLengthMarker);
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
            if (position < 0 || width is < 1 or > sizeof(ulong) || (long)position + width > data.Length)
                throw new InvalidDataException("Truncated binary property list.");
            ulong value = 0;
            for (var i = 0; i < width; i++) value = (value << BitsPerByte) | data[position + i];
            return value;
        }
        private static int Int(ulong value) => value <= int.MaxValue ? (int)value
            : throw new InvalidDataException("Binary property list is too large.");
    }
}
