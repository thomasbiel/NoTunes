using NoTunes.Apple;

namespace NoTunes.Music;

internal sealed class MusicGroupResolver(
    MusicGroupKind kind,
    IReadOnlyList<CatalogueGroup>? rows,
    IReadOnlyDictionary<string, long>? existingIds,
    IReadOnlyDictionary<string, string?> sortNames,
    ISet<long> occupied,
    GroupNamePrompt? prompt)
{
    private const int MaximumNameLength = Mp3Metadata.MaximumTextLength;
    private readonly Dictionary<string, ResolvedMusicGroup> resolved = new(GroupNameComparer.Instance);
    private readonly Dictionary<string, long>? groupIds = existingIds is null ? null : new(existingIds, GroupNameComparer.Instance);
    private readonly Dictionary<string, string?> sorts = new(sortNames, GroupNameComparer.Instance);
    private readonly Dictionary<string, CatalogueGroup[]> rowsByKey = GroupRowsByKey(rows);

    public async Task<ResolvedMusicGroup> ResolveAsync(
        string name,
        string sortName,
        string sourcePath,
        string? albumArtist,
        CancellationToken cancellationToken)
    {
        var originalKey = Key(name, albumArtist);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Key(name, albumArtist);
            if (this.resolved.TryGetValue(key, out var cached))
            {
                this.resolved[originalKey] = cached;
                return cached;
            }

            var result = this.TryResolve(key, name, sortName);
            if (result is not null)
            {
                this.resolved[key] = result;
                this.resolved[originalKey] = result;
                return result;
            }

            var description = this.DescribeConflict(
                key,
                name,
                sourcePath,
                albumArtist);

            name = await this.ChooseNameAsync(description, cancellationToken);
            sortName = name;
        }
    }

    private string DescribeConflict(string key, string name, string sourcePath, string? albumArtist)
    {
        var candidates = this.rowsByKey.GetValueOrDefault(key) ?? [];
        var label = kind switch
        {
            MusicGroupKind.Album => "album",
            MusicGroupKind.AlbumArtist => "album artist",
            MusicGroupKind.Artist => "artist",
            MusicGroupKind.Composer => "composer",
            _ => throw new InvalidOperationException("Unknown music group kind.")
        };

        var qualified = albumArtist is null ? name : name + " / " + albumArtist;
        var conflictingRows = candidates.Length == 1 && candidates[0].SyncId != 0 && rows is not null
            ? rows.Where(group => group.SyncId == candidates[0].SyncId)
            : candidates;

        var conflicts = string.Join(", ", conflictingRows.Select(group =>
            $"'{group.DisplayName}' (row {group.RowId}, sync ID {group.SyncId}, sort '{group.SortName}')"));

        return $"Cannot uniquely assign {label} '{qualified}' for source '{sourcePath}'. "
            + (conflicts.Length == 0 ? "The existing identity or sort name is ambiguous. " : conflicts + ". ")
            + "Enter an unambiguous existing name or a distinct name for a new group. "
            + "This selection applies to this source group for this run. "
            + "Existing tracks and source tags will be preserved.";
    }

    private static Dictionary<string, CatalogueGroup[]> GroupRowsByKey(IReadOnlyList<CatalogueGroup>? rows)
    {
        if (rows is null) return new Dictionary<string, CatalogueGroup[]>(GroupNameComparer.Instance);

        return rows
            .GroupBy(group => group.Key, GroupNameComparer.Instance)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(), GroupNameComparer.Instance);
    }

    private ResolvedMusicGroup? TryResolve(string key, string name, string sortName)
    {
        this.sorts.TryGetValue(key, out var sort);
        if (this.rowsByKey.ContainsKey(key))
        {
            var selected = GroupAliasResolution.Resolve(rows, key);
            return selected is null ? null : new ResolvedMusicGroup(selected.Name, selected.SyncId, selected.SortName);
        }

        if (this.sorts.ContainsKey(key) && sort is null) return null;

        if (this.groupIds is null) return new ResolvedMusicGroup(name, 0, sort ?? sortName);

        if (this.groupIds.TryGetValue(key, out var id))
        {
            return rows is null && id != 0 ? new ResolvedMusicGroup(name, id, sort ?? sortName) : null;
        }

        id = LibraryPreferences.NewId(occupied);
        return new ResolvedMusicGroup(name, id, sort ?? sortName);
    }

    private async Task<string> ChooseNameAsync(string description, CancellationToken cancellationToken)
    {
        while (true)
        {
            var answer = prompt is null ? null : await prompt(description, cancellationToken);
            if (answer is null)
            {
                throw new InvalidDataException(description
                    + " Run in an interactive terminal to resolve this group before staging the new plan.");
            }

            if (string.IsNullOrWhiteSpace(answer))
            {
                throw new OperationCanceledException("Music group selection cancelled.", cancellationToken);
            }

            if (answer.Length <= MaximumNameLength && !answer.Any(char.IsControl)) return answer;

            description = "The target name must contain at most " + MaximumNameLength
                + " characters and no control characters. " + description;
        }
    }

    private static string Key(string name, string? albumArtist)
    {
        return albumArtist is null ? name : CatalogueReader.GroupKey(name, albumArtist);
    }
}
