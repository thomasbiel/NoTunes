namespace NoTunes.Cli;

public static class ExitCodes
{
    public const int Success = 0;
    public const int Error = 1;
    public const int InvalidArguments = 2;
    public const int SyncBlocked = 3;
    public const int PartialSuccess = 5;
    public const int Cancelled = 130;
}
