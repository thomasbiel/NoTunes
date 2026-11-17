namespace NoTunes.Apple;

internal static class BinaryPlistFormat
{
    internal const int TrailerReservedBytes = 6;
    internal const int OffsetWidthOffset = TrailerReservedBytes;
    internal const int ReferenceWidthOffset = OffsetWidthOffset + 1;
    internal const int ObjectCountOffset = ReferenceWidthOffset + 1;
    internal const int TopObjectOffset = ObjectCountOffset + sizeof(ulong);
    internal const int OffsetTableOffset = TopObjectOffset + sizeof(ulong);
    internal const int TrailerBytes = OffsetTableOffset + sizeof(ulong);
    internal const int TypeShift = 4;
    internal const int ExtendedLengthMarker = 15;
    internal const int SingleWidthExponent = 2;
    internal const int DoubleWidthExponent = 3;
    internal const int AsciiCharacterLimit = 128;
    internal const int BitsPerByte = 8;
    internal const byte FalseMarker = 8;
    internal const byte TrueMarker = 9;
    internal const byte DoubleMarker = ((int)BinaryPlistObjectType.Real << TypeShift) | DoubleWidthExponent;
    internal const byte DateMarker = ((int)BinaryPlistObjectType.Date << TypeShift) | DoubleWidthExponent;

    internal static ReadOnlySpan<byte> Signature => "bplist00"u8;

    internal static bool IsAscii(string value)
    {
        return value.AsSpan().IndexOfAnyInRange((char)AsciiCharacterLimit, char.MaxValue) < 0;
    }

    internal static int Width(ulong number)
    {
        return number <= byte.MaxValue ? 1 : number <= ushort.MaxValue ? 2 : number <= uint.MaxValue ? 4 : 8;
    }

    internal static readonly DateTime Epoch = new(
        2001,
        1,
        1,
        0,
        0,
        0,
        DateTimeKind.Utc);
}
