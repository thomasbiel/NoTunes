using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace NoTunes.Apple;

internal sealed class MdnsPacket(byte[] data)
{
    internal const string ServiceName = "_apple-mobdev2._tcp.local";
    internal const int Port = 5353;
    internal const ushort QueryId = 1;
    private const int HeaderBytes = 12;
    private const int FlagsOffset = 2;
    private const int QuestionCountOffset = 4;
    private const int AnswerCountOffset = 6;
    private const int AuthorityCountOffset = 8;
    private const int AdditionalCountOffset = 10;
    private const int RecordHeaderBytes = 10;
    private const int QuestionTrailerBytes = 4;
    private const int SrvHeaderBytes = 6;
    private const int SrvPortOffset = 4;
    private const int MaximumRecords = 1024;
    private const int MaximumNameBytes = 255;
    private const int MaximumLabelBytes = 63;
    private const int MaximumNameSteps = 128;
    private const int Ipv4AddressBytes = 4;
    private const int Ipv6AddressBytes = 16;
    private const ushort ResponseFlag = 0x8000;
    private const ushort OpcodeAndErrorMask = 0x780F;
    private const ushort InternetClass = 1;
    private const ushort ClassMask = 0x7FFF;
    private const byte PointerMask = 0xC0;
    private static readonly Encoding NameEncoding = new UTF8Encoding(false, true);

    internal static byte[] Query(MdnsQuestion question)
    {
        using var stream = new MemoryStream();
        var header = new byte[HeaderBytes];
        BinaryPrimitives.WriteUInt16BigEndian(header, QueryId);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(QuestionCountOffset), 1);
        stream.Write(header);
        foreach (var label in question.Name.Split('.'))
        {
            var bytes = NameEncoding.GetBytes(label);
            if (bytes.Length is < 1 or > MaximumLabelBytes) throw new InvalidDataException("Invalid DNS label.");

            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        stream.WriteByte(0);
        if (stream.Length - HeaderBytes > MaximumNameBytes) throw new InvalidDataException("DNS name is too long.");

        Span<byte> trailer = stackalloc byte[QuestionTrailerBytes];
        BinaryPrimitives.WriteUInt16BigEndian(trailer, (ushort)question.Type);
        BinaryPrimitives.WriteUInt16BigEndian(trailer[sizeof(ushort)..], InternetClass);
        stream.Write(trailer);
        return stream.ToArray();
    }

    internal IReadOnlyList<MdnsRecord> Read(long interfaceIndex)
    {
        this.Require(0, HeaderBytes);
        var flags = this.UInt16(FlagsOffset);
        if (this.UInt16(0) != QueryId || (flags & ResponseFlag) == 0 || (flags & OpcodeAndErrorMask) != 0)
        {
            return [];
        }

        var questions = this.UInt16(QuestionCountOffset);
        var recordCount = this.UInt16(AnswerCountOffset) + this.UInt16(AuthorityCountOffset)
            + this.UInt16(AdditionalCountOffset);

        if (recordCount > MaximumRecords || questions > MaximumRecords)
        {
            throw new InvalidDataException("Too many DNS records.");
        }

        var offset = HeaderBytes;
        for (var index = 0; index < questions; index++)
        {
            this.Name(ref offset);
            this.Require(offset, QuestionTrailerBytes);
            offset += QuestionTrailerBytes;
        }

        var records = new List<MdnsRecord>();
        for (var index = 0; index < recordCount; index++)
        {
            var name = this.Name(ref offset);
            this.Require(offset, RecordHeaderBytes);
            var type = (MdnsRecordType)this.UInt16(offset);
            var recordClass = this.UInt16(offset + sizeof(ushort)) & ClassMask;
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + QuestionTrailerBytes));
            var length = this.UInt16(offset + RecordHeaderBytes - sizeof(ushort));
            offset += RecordHeaderBytes;
            this.Require(offset, length);
            var end = offset + length;
            if (recordClass == InternetClass)
            {
                var record = this.ReadRecord(
                    name,
                    type,
                    ttl,
                    offset,
                    end,
                    interfaceIndex);

                if (record is not null) records.Add(record);
            }

            offset = end;
        }

        return records;
    }

    private MdnsRecord? ReadRecord(
        string name,
        MdnsRecordType type,
        uint ttl,
        int offset,
        int end,
        long interfaceIndex)
    {
        switch (type)
        {
            case MdnsRecordType.Ptr:
                var instance = this.Name(ref offset);
                if (offset != end) throw new InvalidDataException("Invalid DNS PTR length.");

                return new MdnsRecord(
                    name,
                    type,
                    ttl,
                    Target: instance);
            
            case MdnsRecordType.Srv:
                if (end - offset < SrvHeaderBytes) throw new InvalidDataException("Truncated DNS SRV record.");

                var port = this.UInt16(offset + SrvPortOffset);
                offset += SrvHeaderBytes;
                var target = this.Name(ref offset);
                if (offset != end) throw new InvalidDataException("Invalid DNS SRV length.");

                return new MdnsRecord(
                    name,
                    type,
                    ttl,
                    target,
                    port);
            
            case MdnsRecordType.A when end - offset == Ipv4AddressBytes:
                return new MdnsRecord(
                    name,
                    type,
                    ttl,
                    Address: new IPAddress(data.AsSpan(offset, end - offset)));
            
            case MdnsRecordType.Aaaa when end - offset == Ipv6AddressBytes:
                var address = new IPAddress(data.AsSpan(offset, end - offset));
                if (address.IsIPv6LinkLocal) address.ScopeId = interfaceIndex;

                return new MdnsRecord(
                    name,
                    type,
                    ttl,
                    Address: address);
            
            default:
                return null;
        }
    }

    private string Name(ref int offset)
    {
        var position = offset;
        var end = -1;
        var bytes = 1;
        var labels = new List<string>();
        for (var step = 0; step < MaximumNameSteps; step++)
        {
            this.Require(position, 1);
            var length = data[position++];
            if ((length & PointerMask) == PointerMask)
            {
                this.Require(position, 1);
                if (end < 0) end = position + 1;

                position = ((length & MaximumLabelBytes) << 8) | data[position];
                continue;
            }

            if (length > MaximumLabelBytes) throw new InvalidDataException("Invalid DNS name label.");

            if (length == 0)
            {
                offset = end < 0 ? position : end;
                return string.Join('.', labels).ToLowerInvariant();
            }

            bytes += length + 1;
            if (bytes > MaximumNameBytes) throw new InvalidDataException("DNS name is too long.");

            this.Require(position, length);
            var label = NameEncoding.GetString(data, position, length);
            if (label.Contains('.') || label.Contains('\0')) throw new InvalidDataException("Unsupported DNS label.");

            labels.Add(label);
            position += length;
        }

        throw new InvalidDataException("DNS name compression loop.");
    }

    private ushort UInt16(int offset)
    {
        this.Require(offset, sizeof(ushort));
        return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
    }

    private void Require(int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > data.Length - count)
        {
            throw new InvalidDataException("Truncated DNS packet.");
        }
    }
}
