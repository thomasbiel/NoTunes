using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace NoTunes.Apple;

public static class Plist
{
    public const int MaxBytes = 16 * 1024 * 1024;

    public static Dictionary<string, object> Dict(params (string Key, object Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    public static byte[] ToXml(object value)
    {
        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement("plist", new XAttribute("version", "1.0"), ToElement(value)));
        using var output = new MemoryStream();
        document.Save(output, SaveOptions.DisableFormatting);
        return output.ToArray();
    }

    public static object Parse(byte[] data)
    {
        if (data.Length == 0 || data.Length > MaxBytes)
            throw new InvalidDataException("Invalid property list size.");
        if (data.AsSpan().StartsWith("bplist00"u8)) return BinaryPlist.Read(data);
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
            if (document.Root?.Name != "plist" || document.Root.Elements().Count() != 1)
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
        Dictionary<string, object> dictionary => new XElement("dict", dictionary.SelectMany(p =>
            new[] { new XElement("key", p.Key), ToElement(p.Value) })),
        string text => new XElement("string", text),
        bool flag => new XElement(flag ? "true" : "false"),
        byte[] bytes => new XElement("data", Convert.ToBase64String(bytes)),
        int number => new XElement("integer", number.ToString(CultureInfo.InvariantCulture)),
        long number => new XElement("integer", number.ToString(CultureInfo.InvariantCulture)),
        double number => new XElement("real", number.ToString("R", CultureInfo.InvariantCulture)),
        DateTime date => new XElement("date", date.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)),
        IEnumerable<object> items => new XElement("array", items.Select(ToElement)),
        _ => throw new InvalidDataException($"Unsupported property list type: {value.GetType().Name}")
    };

    private static object FromElement(XElement element, int depth)
    {
        if (depth > 64) throw new InvalidDataException("Property list nesting is too deep.");
        switch (element.Name.LocalName)
        {
            case "dict":
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                var children = element.Elements().ToArray();
                if (children.Length % 2 != 0) throw new InvalidDataException("Incomplete property list dictionary.");
                for (var i = 0; i < children.Length; i += 2)
                    if (children[i].Name != "key" || !result.TryAdd(children[i].Value, FromElement(children[i + 1], depth + 1)))
                        throw new InvalidDataException("Invalid or duplicate property list key.");
                return result;
            case "array": return element.Elements().Select(e => FromElement(e, depth + 1)).ToArray();
            case "string": return element.Value;
            case "integer": return long.Parse(element.Value, CultureInfo.InvariantCulture);
            case "real": return double.Parse(element.Value, CultureInfo.InvariantCulture);
            case "true": return true;
            case "false": return false;
            case "data": return Convert.FromBase64String(element.Value);
            case "date": return DateTime.Parse(element.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
            default: throw new InvalidDataException($"Unknown property list type: {element.Name}");
        }
    }
}
