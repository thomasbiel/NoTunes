namespace NoTunes.Apple;

internal enum BinaryPlistObjectType
{
    Simple = 0,
    Integer = 1,
    Real = 2,
    Date = 3,
    Data = 4,
    AsciiString = 5,
    UnicodeString = 6,
    Array = 10,
    Dictionary = 13
}
