namespace NoTunes.Apple;

internal enum AfcOperation : ulong
{
    Status = 1,
    Data = 2,
    GetFileInfo = 10,
    GetDeviceInfo = 11,
    FileOpen = 13,
    FileOpenResult = 14,
    FileRead = 15,
    FileClose = 20
}

internal enum AfcStatusCode : ulong
{
    Success = 0,
    ObjectNotFound = 8
}
