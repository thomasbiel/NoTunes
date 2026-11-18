using CommandLine;
using NoTunes.Apple;
using NoTunes.Cli;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class CommandTests
{
    [Test]
    public void Parses_Sync_Folder_And_Device_Options([Values] bool wifi)
    {
        using var parser = new Parser(settings => settings.HelpWriter = null);
        string[] arguments = ["sync", @"D:\Music collection", "--dry-run", "--device", "00008030-0011223344556677", "--timeout", "30", "--json"];
        var result = parser.ParseArguments<DevicesOptions, ProbeOptions, ScanOptions, SyncOptions>(
            wifi ? [.. arguments, "--wifi"] : arguments);

        Assert.That(result, Is.TypeOf<Parsed<object>>());
        var options = (SyncOptions)((Parsed<object>)result).Value;
        Assert.That(options.Folder, Is.EqualTo(@"D:\Music collection"));
        Assert.That(options.Timeout, Is.EqualTo(30));
        Assert.That(options.DryRun, Is.True);
        Assert.That(options.Json, Is.True);
        Assert.That(options.Device, Is.EqualTo("00008030-0011223344556677"));
        Assert.That(options.Wifi, Is.EqualTo(wifi));
    }

    [TestCase("unknown")]
    public void Rejects_An_Unknown_Command(string command)
    {
        using var parser = new Parser(settings => settings.HelpWriter = null);
        var result = parser.ParseArguments<DevicesOptions, ProbeOptions, ScanOptions, SyncOptions>([command]);
        Assert.That(result, Is.TypeOf<NotParsed<object>>());
    }

    [TestCase("scan")]
    [TestCase("sync")]
    public void Allows_The_Source_Folder_To_Be_Read_From_Configuration(string command)
    {
        using var parser = new Parser(settings => settings.HelpWriter = null);
        var result = parser.ParseArguments<DevicesOptions, ProbeOptions, ScanOptions, SyncOptions>([command]);
        Assert.That(result, Is.TypeOf<Parsed<object>>());
        var options = ((Parsed<object>)result).Value;
        Assert.That(options is ScanOptions scan ? scan.Folder : ((SyncOptions)options).Folder, Is.Null);
    }

    [Test]
    public void Refuses_To_Select_An_Arbitrary_Device()
    {
        UsbDevice[] devices = [new(1, "A", 1), new(2, "B", 1)];
        Assert.Throws<IOException>(() => DeviceOptions.SelectDevice(devices, null));
        Assert.That(DeviceOptions.SelectDevice(devices, "b"), Is.EqualTo(devices[1]));
        Assert.Throws<IOException>(() => DeviceOptions.SelectDevice(devices, "missing"));
        Assert.Throws<IOException>(() => DeviceOptions.SelectDevice([], null));
    }

    [TestCase("00008030-0011223344556677", "000080300011223344556677")]
    [TestCase("00008030-00AABBCCDDEEFF00", "0000803000aabbccddeeff00")]
    [TestCase("0000803000AABBCCDDEEFF00", "00008030-00aabbccddeeff00")]
    [TestCase("0123456789abcdef0123456789abcdef01234567", "0123456789ABCDEF0123456789ABCDEF01234567")]
    public void Selects_Devices_Using_Normalized_Udids(string actual, string requested)
    {
        var device = new UsbDevice(1, actual, 1);
        Assert.That(DeviceOptions.SelectDevice([device], requested), Is.SameAs(device));
    }
}
