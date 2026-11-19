using System.Net;

namespace NoTunes.Apple;

internal sealed record MdnsRecord(
    string Name,
    MdnsRecordType Type,
    uint Ttl,
    string? Target = null,
    ushort Port = 0,
    IPAddress? Address = null);
