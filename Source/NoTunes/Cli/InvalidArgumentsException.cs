namespace NoTunes.Cli;

public sealed class InvalidArgumentsException(string message) : ArgumentException(message);
