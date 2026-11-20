namespace NoTunes.Apple;

internal enum AfcStatusCode : ulong
{
    Success = 0,
    UnknownPacketType = 6,
    ObjectNotFound = 8,
    OperationNotSupported = 15
}
