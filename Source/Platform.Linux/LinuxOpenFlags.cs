using System.Runtime.InteropServices;

namespace NoTunes.Platform.Linux;

internal readonly record struct LinuxOpenFlags(int Directory, int NoFollow)
{
    private const int X64Directory = 1 << 16;
    private const int X64NoFollow = 1 << 17;
    private const int Arm64Directory = 1 << 14;
    private const int Arm64NoFollow = 1 << 15;

    public static LinuxOpenFlags ForArchitecture(Architecture architecture)
    {
        return architecture switch
        {
            Architecture.X64 => new(X64Directory, X64NoFollow),
            Architecture.Arm64 => new(Arm64Directory, Arm64NoFollow),
            _ => throw new PlatformNotSupportedException("The Linux source provider currently supports x64 and ARM64.")
        };
    }
}
