namespace NoTunes.Apple;

internal enum AfcOperation : ulong
{
    Status = 1,
    Data = 2,
    ReadDirectory = 3,
    MakeDirectory = 9,
    GetFileInfo = 10,
    GetDeviceInfo = 11,
    FileOpen = 13,
    FileOpenResult = 14,
    FileRead = 15,
    FileWrite = 16,
    FileClose = 20,
    RenamePath = 24,
    FileLock = 27
}
