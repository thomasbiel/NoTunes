using System.Globalization;
using System.Text;
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

    private static class XmlNames
    {
        public const string Root = "plist";
        public const string Version = "version";
        public const string Dictionary = "dict";
        public const string Key = "key";
        public const string Array = "array";
        public const string String = "string";
        public const string Integer = "integer";
        public const string Real = "real";
        public const string True = "true";
        public const string False = "false";
        public const string Data = "data";
        public const string Date = "date";
    }

    public static Dictionary<string, object> Dict(params (string Key, object Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    public static byte[] ToXml(object value)
    {
        var document = new XDocument(new XDeclaration(XmlVersion, XmlEncodingName, null),
            new XElement(XmlNames.Root, new XAttribute(XmlNames.Version, PlistVersion), ToElement(value)));
        using var output = new MemoryStream();
        document.Save(output, SaveOptions.DisableFormatting);
        return output.ToArray();
    }

    public static object Parse(byte[] data)
    {
        if (data.Length == 0 || data.Length > MaxBytes)
            throw new InvalidDataException("Invalid property list size.");
        if (data.AsSpan().StartsWith(BinaryPlist.Signature)) return BinaryPlist.Read(data);
        try
        {
            using var input = new MemoryStream(data, writable: false);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                MaxCharactersInDocument = MaxBytes
            });
            var document = XDocument.Load(reader);
            if (document.Root?.Name != XmlNames.Root || document.Root.Elements().Count() != 1)
                throw new InvalidDataException("Invalid XML property list.");
            return FromElement(document.Root.Elements().Single(), 0);
        }
        catch (Exception e) when (e is XmlException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Malformed XML property list.", e);
        }
    }

    public static Dictionary<string, object> AsDict(this object value) => value as Dictionary<string, object>
        ?? throw new InvalidDataException("Expected a property list dictionary.");
    public static string Text(this Dictionary<string, object> value, string key) =>
        value.TryGetValue(key, out var item) && item is string text ? text
            : throw new InvalidDataException($"Missing property list string: {key}");
    public static long Number(this Dictionary<string, object> value, string key) =>
        value.TryGetValue(key, out var item) && item is long number ? number
            : throw new InvalidDataException($"Missing property list integer: {key}");
    public static bool Flag(this Dictionary<string, object> value, string key) =>
        value.TryGetValue(key, out var item) && item is true;

    private static XElement ToElement(object value) => value switch
    {
        Dictionary<string, object> dictionary => new XElement(XmlNames.Dictionary, dictionary.SelectMany(p =>
            new[] { new XElement(XmlNames.Key, p.Key), ToElement(p.Value) })),
        string text => new XElement(XmlNames.String, text),
        bool flag => new XElement(flag ? XmlNames.True : XmlNames.False),
        byte[] bytes => new XElement(XmlNames.Data, Convert.ToBase64String(bytes)),
        int number => new XElement(XmlNames.Integer, number.ToString(CultureInfo.InvariantCulture)),
        long number => new XElement(XmlNames.Integer, number.ToString(CultureInfo.InvariantCulture)),
        double number => new XElement(XmlNames.Real, number.ToString(RealFormat, CultureInfo.InvariantCulture)),
        DateTime date => new XElement(XmlNames.Date, date.ToUniversalTime().ToString(DateFormat, CultureInfo.InvariantCulture)),
        IEnumerable<object> items => new XElement(XmlNames.Array, items.Select(ToElement)),
        _ => throw new InvalidDataException($"Unsupported property list type: {value.GetType().Name}")
    };

    private static object FromElement(XElement element, int depth)
    {
        if (depth > MaxNestingDepth) throw new InvalidDataException("Property list nesting is too deep.");
        switch (element.Name.LocalName)
        {
            case XmlNames.Dictionary:
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                var children = element.Elements().ToArray();
                if (children.Length % 2 != 0) throw new InvalidDataException("Incomplete property list dictionary.");
                for (var i = 0; i < children.Length; i += 2)
                    if (children[i].Name != XmlNames.Key || !result.TryAdd(children[i].Value, FromElement(children[i + 1], depth + 1)))
                        throw new InvalidDataException("Invalid or duplicate property list key.");
                return result;
            case XmlNames.Array: return element.Elements().Select(e => FromElement(e, depth + 1)).ToArray();
            case XmlNames.String: return element.Value;
            case XmlNames.Integer: return long.Parse(element.Value, CultureInfo.InvariantCulture);
            case XmlNames.Real: return double.Parse(element.Value, CultureInfo.InvariantCulture);
            case XmlNames.True: return true;
            case XmlNames.False: return false;
            case XmlNames.Data: return Convert.FromBase64String(element.Value);
            case XmlNames.Date: return DateTime.Parse(element.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
            default: throw new InvalidDataException($"Unknown property list type: {element.Name}");
        }
    }
}
