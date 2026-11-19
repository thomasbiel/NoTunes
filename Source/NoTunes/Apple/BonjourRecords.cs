using System.Net;

namespace NoTunes.Apple;

internal sealed class BonjourRecords(TimeProvider? timeProvider = null)
{
    private const int MaximumRecords = 512;
    private const int MaximumEndpoints = 64;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<MdnsRecord, DateTimeOffset> records = [];

    internal void Add(IEnumerable<MdnsRecord> incoming)
    {
        this.RemoveExpired();
        foreach (var record in incoming)
        {
            var key = record with { Ttl = 0 };
            if (record.Ttl == 0)
            {
                this.records.Remove(key);
            }
            else if (this.records.ContainsKey(key) || this.records.Count < MaximumRecords)
            {
                this.records[key] = this.clock.GetUtcNow().AddSeconds(record.Ttl);
            }
        }
    }

    internal IReadOnlyList<IPEndPoint> Endpoints()
    {
        this.RemoveExpired();
        var services = this.Services();
        return this.records.Keys
            .Where(record => record.Address is { } address && DeviceNetworkAddress.IsUnicast(address)
                && !IPAddress.IsLoopback(address) && (!address.IsIPv6LinkLocal || address.ScopeId != 0))
            .SelectMany(record => services.Where(service => service.Target == record.Name)
                .Select(service => new IPEndPoint(record.Address!, service.Port)))
            .Distinct()
            .Take(MaximumEndpoints)
            .ToArray();
    }

    internal IReadOnlyList<MdnsQuestion> Questions()
    {
        this.RemoveExpired();
        var questions = new List<MdnsQuestion> { new(MdnsPacket.ServiceName, MdnsRecordType.Ptr) };
        foreach (var instance in this.Instances())
        {
            if (!this.records.Keys.Any(record => record.Name == instance && record.Type == MdnsRecordType.Srv))
            {
                questions.Add(new MdnsQuestion(instance, MdnsRecordType.Srv));
            }
        }

        foreach (var host in this.Services().Select(service => service.Target!).Distinct())
        {
            foreach (var type in new[] { MdnsRecordType.A, MdnsRecordType.Aaaa })
            {
                if (!this.records.Keys.Any(record => record.Name == host && record.Type == type))
                {
                    questions.Add(new MdnsQuestion(host, type));
                }
            }
        }

        return questions;
    }

    private HashSet<string> Instances()
    {
        return this.records.Keys
            .Where(record => record.Type == MdnsRecordType.Ptr && record.Name == MdnsPacket.ServiceName
                && record.Target is { Length: > 0 })
            .Select(record => record.Target!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private MdnsRecord[] Services()
    {
        var instances = this.Instances();
        return this.records.Keys
            .Where(record => record.Type == MdnsRecordType.Srv && instances.Contains(record.Name)
                && record.Port != 0 && record.Target is { Length: > 0 })
            .ToArray();
    }

    private void RemoveExpired()
    {
        var now = this.clock.GetUtcNow();
        foreach (var key in this.records.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
        {
            this.records.Remove(key);
        }
    }
}
