using static NoTunes.Apple.BinaryPlistFormat;

namespace NoTunes.Apple;

internal sealed class BinaryPlistSize
{
    // Shared immutable strings need no repeated content scan or hash during the prefix search.
    private readonly Dictionary<string, long> stringSizes = new(ReferenceEqualityComparer.Instance);

    public static long Measure(object value)
    {
        return new BinaryPlistSize().GetSize(value);
    }

    public long GetSize(object value)
    {
        var objects = 0;
        long bytes = Signature.Length;
        long references = 0;
        Add(value, 0);
        var table = bytes + references * Width((ulong)objects);
        return table + (long)objects * Width((ulong)table) + TrailerBytes;

        void Add(object item, int depth)
        {
            if (depth > Plist.MaxNestingDepth || objects >= Plist.MaxObjectCount)
            {
                throw new InvalidDataException("Property list is too complex.");
            }

            objects++;
            switch (item)
            {
                case bool:
                    bytes++;
                    break;
                case int number:
                    bytes += IntegerBytes(number);
                    break;
                case long number:
                    bytes += IntegerBytes(number);
                    break;
                case double or DateTime:
                    bytes += 1 + sizeof(long);
                    break;
                case string text:
                    bytes += this.StringBytes(text);
                    break;
                case byte[] data:
                    bytes += LengthBytes(data.Length) + (long)data.Length;
                    break;
                case Dictionary<string, object> dictionary:
                    bytes += LengthBytes(dictionary.Count);
                    references += 2L * dictionary.Count;
                    foreach (var pair in dictionary)
                    {
                        Add(pair.Key, depth + 1);
                        Add(pair.Value, depth + 1);
                    }

                    break;
                case IEnumerable<object> array:
                    var count = 0;
                    foreach (var child in array)
                    {
                        Add(child, depth + 1);
                        count++;
                    }

                    bytes += LengthBytes(count);
                    references += count;
                    break;
                default:
                    throw new InvalidDataException("Unsupported property list type.");
            }
        }
    }

    private long StringBytes(string text)
    {
        if (!this.stringSizes.TryGetValue(text, out var size))
        {
            size = LengthBytes(text.Length) + (long)text.Length * (IsAscii(text) ? sizeof(byte) : sizeof(char));
            this.stringSizes.Add(text, size);
        }

        return size;
    }

    private static int IntegerBytes(long number) => 1 + (number < 0 ? sizeof(long) : Width((ulong)number));

    private static int LengthBytes(int length) => 1 + (length < ExtendedLengthMarker ? 0 : IntegerBytes(length));
}
