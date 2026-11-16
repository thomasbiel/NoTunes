using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace NoTunes.Apple;

public static class Plist
{
    public const int MaxBytes = 16 * 1024 * 1024;
    internal const int MaxNestingDepth = 64;
    internal const int MaxObjectCount = 100_000;
    private const string XmlVersion = "1.0";
    private const string PlistVersion = "1.0";
    private const string XmlEncodingName = "UTF-8";
    private const string RealFormat = "R";
    private const string DateFormat = "yyyy-MM-ddTHH:mm:ssZ";

    public static Dictionary<string, object> Dict(params (string Key, object Value)[] pairs)
    {
        return pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    public static byte[] ToXml(object value)
    {
        var document = new XDocument(new XDeclaration(XmlVersion, XmlEncodingName, null), new XElement(PlistXmlNames.Root, new XAttribute(PlistXmlNames.Version, PlistVersion), ToElement(value)));
        using var output = new MemoryStream();
        document.Save(output, SaveOptions.DisableFormatting);
        return output.ToArray();
    }

    public static object Parse(byte[] data)
    {
        if (data.Length is 0 or > MaxBytes) throw new InvalidDataException("Invalid property list size.");

        if (data.AsSpan().StartsWith(BinaryPlistFormat.Signature))
            return BinaryPlist.Read(data);
        try
        {
            using var input = new MemoryStream(data, writable: false);
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = MaxBytes });
            reader.MoveToContent();
            if (!reader.IsStartElement(PlistXmlNames.Root, string.Empty) || reader.IsEmptyElement)
                throw new InvalidDataException("Invalid XML property list.");

            reader.ReadStartElement();
            var objects = 0;
            var result = ReadValue(reader, 0, ref objects);
            if (reader.MoveToContent() != XmlNodeType.EndElement)
            {
                throw new InvalidDataException("Expected exactly one property list value.");
            }

            reader.ReadEndElement();
            if (reader.MoveToContent() != XmlNodeType.None)
            {
                throw new InvalidDataException("Unexpected content after the property list.");
            }

            return result;
        }
        catch (Exception e) when (e is XmlException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Malformed XML property list.", e);
        }
    }

    public static Dictionary<string, object> AsDict(this object value)
    {
        return value as Dictionary<string, object> ?? throw new InvalidDataException("Expected a property list dictionary.");
    }

    internal static Dictionary<string, object> Dictionary(this Dictionary<string, object> value, string key)
    {
        return value.TryGetValue(key, out var item) ? item.AsDict() : throw new InvalidDataException($"Missing property list dictionary: {key}");
    }

    public static string Text(this Dictionary<string, object> value, string key)
    {
        return value.TryGetValue(key, out var item) && item is string text ? text : throw new InvalidDataException($"Missing property list string: {key}");
    }

    public static long Number(this Dictionary<string, object> value, string key)
    {
        return value.TryGetValue(key, out var item) && item is long number ? number : throw new InvalidDataException($"Missing property list integer: {key}");
    }

    public static bool Flag(this Dictionary<string, object> value, string key)
    {
        return value.TryGetValue(key, out var item) && item is true;
    }

    private static XElement ToElement(object value)
    {
        return value switch
        {
            Dictionary<string, object> dictionary => new XElement(PlistXmlNames.Dictionary, dictionary.SelectMany(p => new[] { new XElement(PlistXmlNames.Key, p.Key), ToElement(p.Value) })),
            string text => new XElement(PlistXmlNames.String, text),
            bool flag => new XElement(flag ? PlistXmlNames.True : PlistXmlNames.False),
            byte[] bytes => new XElement(PlistXmlNames.Data, Convert.ToBase64String(bytes)),
            int number => new XElement(PlistXmlNames.Integer, number.ToString(CultureInfo.InvariantCulture)),
            long number => new XElement(PlistXmlNames.Integer, number.ToString(CultureInfo.InvariantCulture)),
            double number => new XElement(PlistXmlNames.Real, number.ToString(RealFormat, CultureInfo.InvariantCulture)),
            DateTime date => new XElement(PlistXmlNames.Date, date.ToUniversalTime().ToString(DateFormat, CultureInfo.InvariantCulture)),
            IEnumerable<object> items => new XElement(PlistXmlNames.Array, items.Select(ToElement)),
            _ => throw new InvalidDataException($"Unsupported property list type: {value.GetType().Name}")
        };
    }

    private static object ReadValue(XmlReader reader, int depth, ref int objects)
    {
        if (depth > MaxNestingDepth) throw new InvalidDataException("Property list nesting is too deep.");

        CountObject(ref objects);
        if (reader.MoveToContent() != XmlNodeType.Element || reader.NamespaceURI.Length != 0)
        {
            throw new InvalidDataException("Expected a property list value.");
        }

        var type = reader.LocalName;
        if (type == PlistXmlNames.Dictionary) return ReadDictionary(reader, depth, ref objects);
        if (type == PlistXmlNames.Array) return ReadArray(reader, depth, ref objects);
        
        var text = reader.ReadElementContentAsString();
        return type switch
        {
            PlistXmlNames.String => text,
            PlistXmlNames.Integer => long.Parse(text, CultureInfo.InvariantCulture),
            PlistXmlNames.Real => double.Parse(text, CultureInfo.InvariantCulture),
            PlistXmlNames.True when text.Length == 0 => true,
            PlistXmlNames.False when text.Length == 0 => false,
            PlistXmlNames.True or PlistXmlNames.False => throw new InvalidDataException("Property list booleans must be empty elements."),
            PlistXmlNames.Data => Convert.FromBase64String(text),
            PlistXmlNames.Date => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
            _ => throw new InvalidDataException($"Unknown property list type: {type}")
        };
    }

    private static Dictionary<string, object> ReadDictionary(XmlReader reader, int depth, ref int objects)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        var empty = reader.IsEmptyElement;
        reader.ReadStartElement();
        if (empty) return result;
        
        while (reader.MoveToContent() != XmlNodeType.EndElement)
        {
            if (!reader.IsStartElement(PlistXmlNames.Key, string.Empty))
            {
                throw new InvalidDataException("Expected a property list key.");
            }

            CountObject(ref objects);
            var key = reader.ReadElementContentAsString();
            if (!result.TryAdd(key, ReadValue(reader, depth + 1, ref objects)))
            {
                throw new InvalidDataException("Duplicate property list key.");
            }
        }

        reader.ReadEndElement();
        return result;
    }

    private static object[] ReadArray(XmlReader reader, int depth, ref int objects)
    {
        var result = new List<object>();
        var empty = reader.IsEmptyElement;
        reader.ReadStartElement();
        if (empty) return [];
        while (reader.MoveToContent() != XmlNodeType.EndElement)
        {
            result.Add(ReadValue(reader, depth + 1, ref objects));
        }
        
        reader.ReadEndElement();
        return [.. result];
    }

    private static void CountObject(ref int objects)
    {
        if (++objects > MaxObjectCount) throw new InvalidDataException("Property list is too complex.");
    }
}
