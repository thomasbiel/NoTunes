namespace NoTunes.Music;

internal delegate ValueTask<string?> GroupNamePrompt(string description, CancellationToken cancellationToken);
