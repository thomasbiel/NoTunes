using System.Text;
using static NoTunes.Apple.BinaryPlistFormat;

namespace NoTunes.Apple;

internal sealed class BinaryPlistReader(byte[] data)
{
    private const long MaxDecodedBytes = 4L * Plist.MaxBytes;
    private const int EstimatedObjectBytes = 128;
    private const int EstimatedDictionaryEntryBytes = 64;
    private const int DictionaryReferencesPerEntry = 2;
    private const int ArrayReferencesPerEntry = 1;
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
        var validSize = data.Length >= Signature.Length + TrailerBytes && data.Length <= Plist.MaxBytes;
        if (!validSize || !data.AsSpan().StartsWith(Signature)) throw new InvalidDataException("Invalid binary property list.");

        var trailer = data.Length - TrailerBytes;
        this.offsetWidth = data[trailer + OffsetWidthOffset];
        this.refWidth = data[trailer + ReferenceWidthOffset];
        this.count = Int(this.Number(trailer + ObjectCountOffset, sizeof(ulong)));
        var top = Int(this.Number(trailer + TopObjectOffset, sizeof(ulong)));
        this.table = Int(this.Number(trailer + OffsetTableOffset, sizeof(ulong)));
        var validWidths = this.offsetWidth is 1 or 2 or 4 or 8 && this.refWidth is 1 or 2 or 4 or 8;
        var validObjectRange = this.count is >= 1 and <= Plist.MaxObjectCount && top < this.count;
        var validOffsetTable = this.table >= Signature.Length && this.table + (long)this.count * this.offsetWidth <= trailer;
        if (!validWidths || !validObjectRange || !validOffsetTable) throw new InvalidDataException("Invalid binary property list trailer.");

        return this.Object(top, 0);
    }

    private object Object(int index, int depth)
    {
        // Bound reference traversal even when cached objects are shared.
        if (depth > Plist.MaxNestingDepth ||
            ++this.visited > Plist.MaxObjectCount ||
            index < 0 ||
            index >= this.count ||
            !this.active.Add(index))
        {
            throw new InvalidDataException("Cyclic or overly complex binary property list.");
        }

        try
        {
            if (this.cache.TryGetValue((index, depth), out var cached)) return cached;

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
        if (position < Signature.Length || position >= this.table) throw new InvalidDataException("Invalid property list object offset.");

        var marker = data[position++];
        var type = (BinaryPlistObjectType)(marker >> TypeShift);
        var info = marker & ExtendedLengthMarker;
        return type switch
        {
            BinaryPlistObjectType.Simple when info == FalseMarker => false,
            BinaryPlistObjectType.Simple when info == TrueMarker => true,
            BinaryPlistObjectType.Integer => this.ReadInteger(position, info),
            BinaryPlistObjectType.Real when info is SingleWidthExponent or DoubleWidthExponent => this.ReadReal(position, info),
            BinaryPlistObjectType.Date when info == DoubleWidthExponent => this.ReadDate(position),
            BinaryPlistObjectType.Data => this.ReadData(position, info),
            BinaryPlistObjectType.AsciiString or BinaryPlistObjectType.UnicodeString => this.ReadString(position, info, type),
            BinaryPlistObjectType.Array => this.ReadArray(position, info, depth),
            BinaryPlistObjectType.Dictionary => this.ReadDictionary(position, info, depth),
            _ => throw new InvalidDataException($"Unsupported binary property list type: 0x{marker:X2}")
        };
    }

    private long ReadInteger(int position, int info)
    {
        if (info > DoubleWidthExponent) throw new InvalidDataException("Property list integer exceeds 64 bits.");

        var width = 1 << info;
        this.Slice(position, width);
        return unchecked((long)this.Number(position, width));
    }

    private double ReadReal(int position, int info)
    {
        this.Slice(position, 1 << info);
        return info == SingleWidthExponent
            ? BitConverter.Int32BitsToSingle(unchecked((int)this.Number(position, sizeof(float))))
            : BitConverter.Int64BitsToDouble(unchecked((long)this.Number(position, sizeof(double))));
    }

    private DateTime ReadDate(int position)
    {
        this.Slice(position, sizeof(ulong));
        try
        {
            var encodedSeconds = unchecked((long)this.Number(position, sizeof(double)));
            return Epoch.AddSeconds(BitConverter.Int64BitsToDouble(encodedSeconds));
        }
        catch (ArgumentException e)
        {
            throw new InvalidDataException("Invalid property list date.", e);
        }
    }

    private byte[] ReadData(int position, int info)
    {
        var byteCount = this.Length(info, ref position);
        var bytes = this.Slice(position, byteCount);
        this.ReserveDecodedBytes(byteCount);
        return [.. bytes];
    }

    private string ReadString(int position, int info, BinaryPlistObjectType type)
    {
        var length = this.Length(info, ref position);
        var encoding = type == BinaryPlistObjectType.UnicodeString ? Encoding.BigEndianUnicode : Encoding.ASCII;
        var characterBytes = type == BinaryPlistObjectType.UnicodeString ? sizeof(char) : sizeof(byte);
        var bytes = this.Slice(position, checked(length * characterBytes));
        this.ReserveDecodedBytes((long)length * sizeof(char));
        return encoding.GetString(bytes);
    }

    private object[] ReadArray(int position, int info, int depth)
    {
        var entries = this.ReadCollectionLength(info, ref position, dictionary: false);
        var array = new object[entries];
        for (var i = 0; i < entries; i++)
        {
            array[i] = this.Object(this.Reference(position + i * this.refWidth), depth + 1);
        }

        return array;
    }

    private Dictionary<string, object> ReadDictionary(int position, int info, int depth)
    {
        var entries = this.ReadCollectionLength(info, ref position, dictionary: true);
        var dictionary = new Dictionary<string, object>(StringComparer.Ordinal);
        for (var i = 0; i < entries; i++)
        {
            var key = this.Object(this.Reference(position + i * this.refWidth), depth + 1) as string
                ?? throw new InvalidDataException("Property list key is not a string.");
            var value = this.Object(this.Reference(position + (entries + i) * this.refWidth), depth + 1);
            if (!dictionary.TryAdd(key, value)) throw new InvalidDataException("Duplicate property list key.");
        }

        return dictionary;
    }

    private int ReadCollectionLength(int info, ref int position, bool dictionary)
    {
        var entries = this.Length(info, ref position);
        if (entries > Plist.MaxObjectCount) throw new InvalidDataException("Too many property list entries.");

        var referencesPerEntry = dictionary ? DictionaryReferencesPerEntry : ArrayReferencesPerEntry;
        var decodedEntryBytes = dictionary ? EstimatedDictionaryEntryBytes : IntPtr.Size;
        this.Slice(position, checked(entries * this.refWidth * referencesPerEntry));
        this.ReserveDecodedBytes((long)entries * decodedEntryBytes);
        return entries;
    }

    private void ReserveDecodedBytes(long bytes)
    {
        if (bytes > MaxDecodedBytes - this.decodedBytes) throw new InvalidDataException("Decoded binary property list is too large.");
        this.decodedBytes += bytes;
    }

    private int Reference(int position) => Int(this.Number(position, this.refWidth));

    private int Length(int info, ref int position)
    {
        if (info != ExtendedLengthMarker) return info;

        this.Slice(position, 1);
        var marker = data[position++];
        var isInteger = (BinaryPlistObjectType)(marker >> TypeShift) == BinaryPlistObjectType.Integer;
        var supportedWidth = (marker & ExtendedLengthMarker) <= DoubleWidthExponent;
        if (!isInteger || !supportedWidth) throw new InvalidDataException("Invalid property list length.");

        var width = 1 << (marker & ExtendedLengthMarker);
        this.Slice(position, width);
        var length = Int(this.Number(position, width));
        position += width;
        return length;
    }

    private ReadOnlySpan<byte> Slice(int start, int length)
    {
        if (start < 0 || length < 0 || (long)start + length > this.table) throw new InvalidDataException("Truncated binary property list.");

        return data.AsSpan(start, length);
    }

    private ulong Number(int position, int width)
    {
        if (position < 0 || width is < 1 or > sizeof(ulong) || (long)position + width > data.Length)
        {
            throw new InvalidDataException("Truncated binary property list.");
        }

        ulong value = 0;
        for (var i = 0; i < width; i++)
        {
            value = (value << BitsPerByte) | data[position + i];
        }

        return value;
    }

    private static int Int(ulong value)
    {
        return value <= int.MaxValue ? (int)value : throw new InvalidDataException("Binary property list is too large.");
    }
}
