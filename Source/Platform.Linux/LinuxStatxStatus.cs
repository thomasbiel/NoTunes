using System.Runtime.InteropServices;

namespace NoTunes.Platform.Linux;

[StructLayout(LayoutKind.Explicit, Size = LinuxStatxStatus.StatxBufferBytes)]
internal struct LinuxStatxStatus
{
    internal const int StatxBufferBytes = 256;
    internal const int StatxModeOffset = 28;
    [FieldOffset(0)]
    public uint Mask;
    [FieldOffset(StatxModeOffset)]
    public ushort Mode;
}
