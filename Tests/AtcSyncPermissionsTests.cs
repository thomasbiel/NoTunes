using NoTunes.Apple;
using NUnit.Framework;

namespace NoTunes.Tests;

public sealed class AtcSyncPermissionsTests
{
    [TestCase(false, false, false, false, false)]
    [TestCase(true, false, false, false, true)]
    [TestCase(false, true, false, false, true)]
    [TestCase(true, true, false, false, true)]
    [TestCase(true, false, true, false, false)]
    [TestCase(false, true, true, false, false)]
    [TestCase(true, false, false, true, false)]
    [TestCase(false, true, false, true, false)]
    public void ValidatesSyncPermissions(bool manual, bool auto, bool protectedData, bool musicDisabled, bool expected)
    {
        var parameters = Plist.Dict(("ManualSync", manual), ("AutoSync", auto), ("DataProtected", protectedData),
            ("DisabledAssetTypes", musicDisabled ? new object[] { "Music" } : new object[] { "Video" }));
        var result = AtcClient.ReadSyncPermissions(parameters);
        Assert.That(result.Allowed, Is.EqualTo(expected));
        Assert.That(result.DataProtected, Is.EqualTo(protectedData));
    }

    [Test]
    public void MissingOptionalFieldsKeepTheirDefaults()
    {
        Assert.That(AtcClient.ReadSyncPermissions([]).Allowed, Is.False);
        Assert.That(AtcClient.ReadSyncPermissions(Plist.Dict(("ManualSync", true))), Is.EqualTo((true, false)));
    }

    [TestCase("ManualSync")]
    [TestCase("AutoSync")]
    [TestCase("DataProtected")]
    public void RejectsWrongBooleanTypesEvenWhenAnotherFlagAlreadyAllowsSync(string key)
    {
        object?[] invalidValues = ["true", 1L, Array.Empty<object>(), Plist.Dict(), null];
        foreach (var invalid in invalidValues)
        {
            var parameters = Plist.Dict(("ManualSync", true), ("AutoSync", true), ("DataProtected", false));
            parameters[key] = invalid!;
            Assert.That(() => AtcClient.ReadSyncPermissions(parameters),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains(key));
        }
    }

    [Test]
    public void RejectsMalformedDisabledAssetTypesEvenWhenSyncIsAlreadyBlocked()
    {
        object?[] invalidValues = ["Music", 1L, true, Plist.Dict(), new object[] { "Music", 1L },
            new object[] { null! }, null];
        foreach (var invalid in invalidValues)
        {
            var parameters = Plist.Dict(("ManualSync", false), ("DisabledAssetTypes", invalid!));
            Assert.That(() => AtcClient.ReadSyncPermissions(parameters),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("DisabledAssetTypes"));
        }
    }
}
