using NoTunes.Apple;
using NoTunes.Cli;
using NoTunes.Music;
using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class DeviceSettingsTests
{
    private const string DefaultUdid = "00008030-00112CE23A86402E";
    private const string DefaultAddress = "192.168.0.42";
    private SyncWorkspace workspace = null!;

    private string ConfigurationFile => Path.Combine(this.workspace.Root, "test.config");

    [SetUp]
    public void SetUp()
    {
        this.workspace = new SyncWorkspace();
    }

    [TearDown]
    public void TearDown()
    {
        this.workspace.Dispose();
    }

    [Test]
    public void Uses_The_Configured_Device_And_Only_Applies_The_Address_For_Wifi([Values] bool wifi)
    {
        this.WriteDefaults();
        var options = new ProbeOptions { Wifi = wifi };
        DeviceSettings.ApplyDefaults(options, this.ConfigurationFile);
        Assert.That(options.Device, Is.EqualTo(DefaultUdid));
        Assert.That(options.Address, Is.EqualTo(wifi ? DefaultAddress : null));
    }

    [Test]
    public void Command_Line_Values_Override_Each_Default_Independently(
        [Values] bool deviceOverride,
        [Values] bool addressOverride)
    {
        this.WriteDefaults();
        var options = new ProbeOptions
        {
            Wifi = true,
            Device = deviceOverride ? "other-phone" : null,
            Address = addressOverride ? "192.168.0.43" : null
        };

        DeviceSettings.ApplyDefaults(options, this.ConfigurationFile);
        Assert.That(options.Device, Is.EqualTo(deviceOverride ? "other-phone" : DefaultUdid));
        Assert.That(options.Address, Is.EqualTo(addressOverride ? "192.168.0.43" : DefaultAddress));
    }

    [Test]
    public void Explicit_Values_Do_Not_Read_Unused_Configuration([Values] bool wifi)
    {
        File.WriteAllText(this.ConfigurationFile, "<configuration>");
        var options = new ProbeOptions { Wifi = wifi, Device = "explicit-phone", Address = "192.168.0.43" };
        Assert.That(() => DeviceSettings.ApplyDefaults(options, this.ConfigurationFile), Throws.Nothing);
        Assert.That(options.Device, Is.EqualTo("explicit-phone"));
        Assert.That(options.Address, Is.EqualTo("192.168.0.43"));
    }

    [TestCase("")]
    [TestCase("<add key=\"DeviceUdid\" value=\" \"/><add key=\"DeviceAddress\" value=\" \"/>")]
    public void Missing_Or_Blank_Defaults_Preserve_Device_Discovery(string entries)
    {
        File.WriteAllText(this.ConfigurationFile, $"<configuration><appSettings>{entries}</appSettings></configuration>");
        var options = new ProbeOptions { Wifi = true };
        DeviceSettings.ApplyDefaults(options, this.ConfigurationFile);
        Assert.That(options.Device, Is.Null);
        Assert.That(options.Address, Is.Null);
    }

    [Test]
    public void Reports_Malformed_Configuration_When_A_Default_Is_Needed()
    {
        File.WriteAllText(this.ConfigurationFile, "<configuration>");
        Assert.That(() => DeviceSettings.ApplyDefaults(new ProbeOptions(), this.ConfigurationFile),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("application configuration"));
    }

    [Test]
    public async Task Wifi_Defaults_Select_The_Configured_Address_Without_Usb_Discovery()
    {
        this.WriteDefaults();
        var options = new SyncOptions { Wifi = true, Timeout = 1 };
        DeviceSettings.ApplyDefaults(options, this.ConfigurationFile);
        var transport = new ReplyUsbMuxTransport();
        var device = await options.SelectDeviceAsync(new UsbMuxClient(transport, TimeSpan.FromSeconds(1)),
            CancellationToken.None);

        Assert.That(device.Udid, Is.EqualTo(DefaultUdid));
        Assert.That(device.NetworkAddress, Is.EqualTo(DefaultAddress));
        Assert.That(transport.Connections, Is.Zero);
    }

    [Test]
    public async Task Direct_Wifi_Addresses_Still_Require_A_Pairing_Identity_Without_A_Default(
        [Values(typeof(DevicesOptions), typeof(ProbeOptions), typeof(SyncOptions), typeof(InitOptions))] Type optionType)
    {
        File.WriteAllText(this.ConfigurationFile, "<configuration><appSettings /></configuration>");
        var options = (DeviceOptions)Activator.CreateInstance(optionType)!;
        options.Address = DefaultAddress;
        options.Timeout = 1;
        DeviceSettings.ApplyDefaults(options, this.ConfigurationFile);
        var mux = new UsbMuxClient(new ReplyUsbMuxTransport(), TimeSpan.FromSeconds(1));
        await Assert.ThatAsync(() => options.SelectDeviceAsync(mux, CancellationToken.None),
            Throws.TypeOf<InvalidArgumentsException>().With.Message.Contains("--device"));
    }

    private void WriteDefaults()
    {
        File.WriteAllText(this.ConfigurationFile, $"""
            <configuration><appSettings>
              <add key="DeviceUdid" value="{DefaultUdid}" />
              <add key="DeviceAddress" value="{DefaultAddress}" />
            </appSettings></configuration>
            """);
    }
}
