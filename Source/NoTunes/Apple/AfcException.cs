namespace NoTunes.Apple;

public sealed class AfcException(ulong code) : IOException($"The iPhone file service returned AFC error {code}.")
{
    public ulong Code { get; } = code;
}
