using NoTunes.Apple;
using NoTunes.Music;

namespace NoTunes.Tests;

internal static class SyncTestData
{
    public const string FixtureName = "tone-mp3-vbr.mp3";
    public const long TrackId = -2001;

    public static string FixturePath => Path.Combine(NUnit.Framework.TestContext.CurrentContext.TestDirectory, "Fixtures", FixtureName);

    public static PreparedTrack Track()
    {
        var bytes = File.ReadAllBytes(FixturePath);
        var source = new SourceTrack(
            FixtureName,
            bytes.Length,
            File.GetLastWriteTimeUtc(FixturePath),
            ContentHash.Compute(bytes));

        return new PreparedTrack(
            source,
            FixturePath,
            Mp3Metadata.Read(FixturePath, FixtureName),
            TrackId,
            1012,
            1011);
    }

    public static Dictionary<string, object> Message(string command, Dictionary<string, object>? parameters = null, long session = SyncProtocol.Session)
    {
        return Plist.Dict((AtcKeys.Command, command), (AtcKeys.Session, session), (AtcKeys.Parameters, parameters ?? Plist.Dict()));
    }

    public static Dictionary<string, object> Capabilities()
    {
        return Message(AtcCommands.Capabilities, Plist.Dict((AtcKeys.Capabilities, Plist.Dict(
            (AtcKeys.CapabilityVersion, SyncProtocol.CapabilityVersion), (AtcKeys.ProtocolVersion, SyncProtocol.CapabilityVersion)))));
    }

    public static Dictionary<string, object> Allowed()
    {
        return Message(AtcCommands.SyncAllowed, Plist.Dict((AtcKeys.ManualSync, true), (AtcKeys.DataProtected, false)));
    }

    public static Dictionary<string, object> Ready(string anchor = "7")
    {
        return Message(AtcCommands.ReadyForSync, Plist.Dict(
            (AtcKeys.Anchors, Plist.Dict((AtcKeys.Media, anchor))),
            (AtcKeys.DeviceInfo, Plist.Dict((AtcKeys.Grappa, GrappaTestData.Reply())))));
    }

    public static Dictionary<string, object>[] SessionMessages(params Dictionary<string, object>[] messages)
    {
        return [Capabilities(), Allowed(), Ready(), .. messages];
    }

    public static Dictionary<string, object> Asset(string id, long parts = SyncProtocol.AudioPart)
    {
        return Plist.Dict(
            (AtcKeys.AssetId, id),
            (AtcKeys.AssetType, AtcKeys.Music),
            (AtcKeys.IsDownload, true),
            (AtcKeys.Variant, Plist.Dict((AtcKeys.AssetParts, parts))));
    }

    public static Dictionary<string, object> Manifest(params object[] entries)
    {
        return Plist.Dict((AtcKeys.AssetManifest, Plist.Dict((AtcKeys.Media, entries))));
    }
}
