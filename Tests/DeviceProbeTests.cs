using NoTunes.Apple;
using NoTunes.Cli;
using NUnit.Framework;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NoTunes.Tests;

[TestFixture]
public sealed class DeviceProbeTests
{
    [Test]
    public void Probe_Result_Preserves_The_Public_Json_Contract()
    {
        var result = CreateResult();
        var expected = JsonNode.Parse("""
            {
              "Device": { "Udid": "test-udid", "Name": "Test phone", "Model": "iPhone12,8", "IosVersion": "27.2", "BuildVersion": "24B119" },
              "Paired": true, "AfcAvailable": true, "MusicDatabasePresent": true, "AtcAvailable": true,
              "SyncAllowed": false, "DataProtected": true, "MusicTrackCount": 42, "MusicBytes": null,
              "FreeBytes": "1024", "CatalogueReadVerified": true, "CatalogueBytes": 256, "CatalogueWalBytes": null
            }
            """);

        var actual = JsonSerializer.SerializeToNode(result);
        Assert.That(JsonNode.DeepEquals(actual, expected), Is.True, actual?.ToJsonString());
    }

    [Test]
    public async Task Text_Output_Aligns_Every_Value_In_The_Second_Column(
        [Values] bool network,
        [Values] bool includeOptionalDetails)
    {
        var result = CreateResult() with
        {
            MusicTrackCount = includeOptionalDetails ? 42 : null,
            FreeBytes = includeOptionalDetails ? "1024" : null,
            CatalogueReadVerified = includeOptionalDetails
        };

        await using var output = new StringWriter();
        var connectionType = network ? "Network" : "USB";
        await ProbeOptions.WriteTextAsync(output, result, connectionType);
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.That(lines, Has.Length.EqualTo(includeOptionalDetails ? 9 : 6));
        foreach (var line in lines)
        {
            var labelEnd = line.IndexOf(':') + 1;
            Assert.That(labelEnd, Is.GreaterThan(0), line);
            var valueStart = labelEnd;
            while (valueStart < line.Length && line[valueStart] == ' ')
            {
                valueStart++;
            }

            Assert.That(valueStart, Is.EqualTo(18), line);
        }

        Assert.That(lines, Does.Contain("Connection:       " + (network ? "Wi-Fi" : "USB")));
        Assert.That(lines, Does.Contain("Device ID:        test-udid"));
        Assert.That(output.ToString(), Does.Not.Contain("AFC").And.Not.Contain("ATC").And.Not.Contain("WAL"));
        Assert.That(output.ToString(), Does.Not.Contain("pairing:").And.Not.Contain("Music changed:").And.Not.Contain("OK"));
        if (includeOptionalDetails)
        {
            Assert.That(lines, Does.Contain("Database size:    256 B"));
        }
    }

    [Test]
    public async Task Formats_Large_Track_Counts_And_Storage_Quantities_For_Text_Output()
    {
        var result = CreateResult() with
        {
            MusicTrackCount = 12345,
            FreeBytes = "12345678900",
            CatalogueBytes = 16486400,
            CatalogueReadVerified = true
        };

        using var output = new StringWriter();
        await ProbeOptions.WriteTextAsync(output, result, "Network");
        Assert.That(output.ToString(), Does.Contain("Music tracks:     12,345"));
        Assert.That(output.ToString(), Does.Contain("Free space:       12.3 GB"));
        Assert.That(output.ToString(), Does.Contain("Database size:    16.5 MB"));
    }

    [Test]
    public async Task Text_Output_Preserves_Variable_Library_And_Sync_Status(
        [Values] bool initialized,
        [Values] bool syncAllowed)
    {
        var result = CreateResult() with { MusicDatabasePresent = initialized, SyncAllowed = syncAllowed };
        await using var output = new StringWriter();
        await ProbeOptions.WriteTextAsync(output, result, "USB");
        Assert.That(output.ToString(), Does.Contain("Music library:    " + (initialized ? "present" : "not initialized")));
        Assert.That(output.ToString(), Does.Contain("Music sync:       " + (syncAllowed ? "allowed" : "blocked by iPhone")));
    }

    private static ProbeResult CreateResult()
    {
        return new ProbeResult
        {
            Device = new DeviceInfo(
                "test-udid",
                "Test phone",
                "iPhone12,8",
                "27.2",
                "24B119"),
            Paired = true,
            AfcAvailable = true,
            MusicDatabasePresent = true,
            AtcAvailable = true,
            SyncAllowed = false,
            DataProtected = true,
            MusicTrackCount = 42,
            MusicBytes = null,
            FreeBytes = "1024",
            CatalogueReadVerified = true,
            CatalogueBytes = 256,
            CatalogueWalBytes = null
        };
    }
}
