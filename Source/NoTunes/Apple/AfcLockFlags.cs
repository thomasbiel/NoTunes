namespace NoTunes.Apple;

[Flags]
internal enum AfcLockFlags : ulong
{
    Exclusive = 2,
    NonBlocking = 4
}
