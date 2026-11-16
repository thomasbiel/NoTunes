using System.Text;
using static NoTunes.Apple.BinaryPlistFormat;

namespace NoTunes.Apple;

internal sealed class BinaryPlistReader(byte[] data)
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
        this.offsetWidth = data[trailer + OffsetWidthOffset];
        this.refWidth = data[trailer + ReferenceWidthOffset];
        this.count = Int(this.Number(trailer + ObjectCountOffset, sizeof(ulong)));
        var top = Int(this.Number(trailer + TopObjectOffset, sizeof(ulong)));
        this.table = Int(this.Number(trailer + OffsetTableOffset, sizeof(ulong)));
        if (this.offsetWidth is not (1 or 2 or 4 or 8) || this.refWidth is not (1 or 2 or 4 or 8) || this.count < 1 || this.count > Plist.MaxObjectCount || top >= this.count || this.table < Signature.Length || (long)this.table + (long)this.count * this.offsetWidth > trailer)
            throw new InvalidDataException("Invalid binary property list trailer.");

        return this.Object(top, 0);
    }

    private object Object(int index, int depth)
    {
        // Bound reference traversal even when cached objects are shared.
        if (depth > Plist.MaxNestingDepth || ++this.visited > Plist.MaxObjectCount || index < 0 || index >= this.count || !this.active.Add(index))
            throw new InvalidDataException("Cyclic or overly complex binary property list.");

        try
        {
            if (this.cache.TryGetValue((index, depth), out var cached))
                return cached;
            this.ReserveDecodedBytes(EstimatedObjectBytes);
            var value = this.ReadObject(index, depth);
            this.cache.Add((index, depth), value);
            return value;
        }
        catch (OverflowException e)
        {
            throw new InvalidDataException("Property list size overflow.", e);
        }
        finally
        {
            this.active.Remove(index);
        }
    }

    private object ReadObject(int index, int depth)
    {
        var position = Int(this.Number(this.table + index * this.offsetWidth, this.offsetWidth));
        if (position < Signature.Length || position >= this.table)
            throw new InvalidDataException("Invalid property list object offset.");

        var marker = data[position++];
        var type = (BinaryPlistObjectType)(marker >> TypeShift);
        var info = marker & ExtendedLengthMarker;
        switch (type)
        {
            case BinaryPlistObjectType.Simple when info == FalseMarker:
                return false;
            case BinaryPlistObjectType.Simple when info == TrueMarker:
                return true;
            case BinaryPlistObjectType.Integer:
                if (info > DoubleWidthExponent)
                    throw new InvalidDataException("Property list integer exceeds 64 bits.");
                this.Slice(position, 1 << info);
                return unchecked((long)this.Number(position, 1 << info));
            case BinaryPlistObjectType.Real when info is SingleWidthExponent or DoubleWidthExponent:
                this.Slice(position, 1 << info);
                return info == SingleWidthExponent ? (double)BitConverter.Int32BitsToSingle(unchecked((int)this.Number(position, sizeof(float)))) : BitConverter.Int64BitsToDouble(unchecked((long)this.Number(position, sizeof(double))));
            case BinaryPlistObjectType.Date when info == DoubleWidthExponent:
                this.Slice(position, sizeof(ulong));
                try
                {
                    return Epoch.AddSeconds(BitConverter.Int64BitsToDouble(unchecked((long)this.Number(position, sizeof(double)))));
                }
                catch (ArgumentException e)
                {
                    throw new InvalidDataException("Invalid property list date.", e);
                }

            case BinaryPlistObjectType.Data:
                var byteCount = this.Length(info, ref position);
                var dataBytes = this.Slice(position, byteCount);
                this.ReserveDecodedBytes(byteCount);
                return dataBytes.ToArray();
            case BinaryPlistObjectType.AsciiString:
            case BinaryPlistObjectType.UnicodeString:
                var length = this.Length(info, ref position);
                var bytes = this.Slice(position, checked(length * (type == BinaryPlistObjectType.UnicodeString ? sizeof(char) : sizeof(byte))));
                this.ReserveDecodedBytes((long)length * sizeof(char));
                return (type == BinaryPlistObjectType.UnicodeString ? Encoding.BigEndianUnicode : Encoding.ASCII).GetString(bytes);
            case BinaryPlistObjectType.Array:
            case BinaryPlistObjectType.Dictionary:
                var entries = this.Length(info, ref position);
                if (entries > Plist.MaxObjectCount) throw new InvalidDataException("Too many property list entries.");
                this.Slice(position, checked(entries * this.refWidth * (type == BinaryPlistObjectType.Dictionary ? 2 : 1)));
                this.ReserveDecodedBytes((long)entries * (type == BinaryPlistObjectType.Array ? IntPtr.Size : EstimatedDictionaryEntryBytes));
                if (type == BinaryPlistObjectType.Array)
                {
                    var array = new object[entries];
                    for (var i = 0; i < entries; i++)
                        array[i] = this.Object(this.Reference(position + i * this.refWidth), depth + 1);
                    return array;
                }

                var dictionary = new Dictionary<string, object>(StringComparer.Ordinal);
                for (var i = 0; i < entries; i++)
                {
                    var key = this.Object(this.Reference(position + i * this.refWidth), depth + 1) as string ?? throw new InvalidDataException("Property list key is not a string.");
                    var value = this.Object(this.Reference(position + (entries + i) * this.refWidth), depth + 1);
                    if (!dictionary.TryAdd(key, value)) throw new InvalidDataException("Duplicate property list key.");
                }

                return dictionary;
            default:
                throw new InvalidDataException($"Unsupported binary property list type: 0x{marker:X2}");
        }
    }

    private void ReserveDecodedBytes(long bytes)
    {
        if (bytes > MaxDecodedBytes - this.decodedBytes)
            throw new InvalidDataException("Decoded binary property list is too large.");

        this.decodedBytes += bytes;
    }

    private int Reference(int position) => Int(this.Number(position, this.refWidth));

    private int Length(int info, ref int position)
    {
        if (info != ExtendedLengthMarker)
            return info;
        this.Slice(position, 1);
        var marker = data[position++];
        if ((BinaryPlistObjectType)(marker >> TypeShift) != BinaryPlistObjectType.Integer || (marker & ExtendedLengthMarker) > DoubleWidthExponent)
            throw new InvalidDataException("Invalid property list length.");

        var width = 1 << (marker & ExtendedLengthMarker);
        this.Slice(position, width);
        var length = Int(this.Number(position, width));
        position += width;
        return length;
    }

    private ReadOnlySpan<byte> Slice(int start, int length)
    {
        if (start < 0 || length < 0 || (long)start + length > this.table)
            throw new InvalidDataException("Truncated binary property list.");

        return data.AsSpan(start, length);
    }

    private ulong Number(int position, int width)
    {
        if (position < 0 || width is < 1 or > sizeof(ulong) || (long)position + width > data.Length)
            throw new InvalidDataException("Truncated binary property list.");

        ulong value = 0;
        for (var i = 0; i < width; i++)
            value = (value << BitsPerByte) | data[position + i];
        return value;
    }

    private static int Int(ulong value)
    {
        return value <= int.MaxValue ? (int)value : throw new InvalidDataException("Binary property list is too large.");
    }
}
