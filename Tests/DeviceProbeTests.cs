using NoTunes.Apple;
using NUnit.Framework;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NoTunes.Tests;

public sealed class DeviceProbeTests
{
    [Test]
    public void ProbeResultPreservesThePublicJsonContract()
    {
        var result = new ProbeResult
        {
            Device = new DeviceInfo("test-udid", "Test phone", "iPhone12,8", "27.2", "24B119"),
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
}
