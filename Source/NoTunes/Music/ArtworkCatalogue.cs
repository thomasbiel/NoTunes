using System.Data.SQLite;
using System.Globalization;
using NoTunes.Apple;

namespace NoTunes.Music;

internal static class ArtworkCatalogue
{
    private const int RequiredTableCount = 3;
    private const int LocalArtworkSource = 300;
    private const int TrackEntityType = 0;
    private const int TrackArtworkType = 1;
    private const int DefaultArtworkVariant = 0;
    private const int IdColumn = 0;
    private const int TrackSyncIdColumn = 0;
    private const int TrackTokenColumn = 1;
    private const int TrackPathColumn = 2;
    private const string TablesSql = """
        SELECT count(*) FROM sqlite_schema WHERE type='table'
        AND name IN ('artwork','artwork_token','best_artwork_token')
        """;

    private const string IdsSql = """
        SELECT artwork_token FROM artwork WHERE artwork_source_type=$localSource
        UNION SELECT artwork_token FROM artwork_token WHERE artwork_source_type=$localSource
        """;

    private const string TracksSql = """
        SELECT s.sync_id,t.artwork_token,
          CASE WHEN EXISTS(SELECT 1 FROM best_artwork_token b
            WHERE b.entity_pid=t.entity_pid AND b.entity_type=t.entity_type
              AND b.available_artwork_token=t.artwork_token
              AND b.artwork_type=t.artwork_type AND b.artwork_variant_type=t.artwork_variant_type)
          THEN a.relative_path ELSE NULL END
        FROM artwork_token t JOIN item_store s ON s.item_pid=t.entity_pid
        LEFT JOIN artwork a ON a.artwork_token=t.artwork_token
          AND a.artwork_source_type=t.artwork_source_type AND a.artwork_variant_type=t.artwork_variant_type
        WHERE s.sync_id<>0 AND t.artwork_source_type=$localSource AND t.entity_type=$entityType
          AND t.artwork_type=$artworkType AND t.artwork_variant_type=$variant
        ORDER BY s.sync_id
        """;

    public static DeviceCatalogue Read(SQLiteConnection connection, DeviceCatalogue catalogue)
    {
        using var command = connection.CreateCommand();
        command.CommandText = TablesSql;
        var count = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (count == 0) return catalogue;
        if (count != RequiredTableCount) throw new InvalidDataException("Incomplete artwork catalogue schema.");

        command.CommandText = IdsSql;
        command.Parameters.AddWithValue("$localSource", LocalArtworkSource);
        var highest = catalogue.ArtworkHighWaterMark;
        using (var rows = command.ExecuteReader())
        {
            while (rows.Read())
            {
                highest = Math.Max(highest, ParseId(rows.GetString(IdColumn)));
            }
        }

        command.CommandText = TracksSql;
        command.Parameters.AddWithValue("$entityType", TrackEntityType);
        command.Parameters.AddWithValue("$artworkType", TrackArtworkType);
        command.Parameters.AddWithValue("$variant", DefaultArtworkVariant);
        var tracks = new Dictionary<long, ArtworkReference>();
        using (var rows = command.ExecuteReader())
        {
            while (rows.Read())
            {
                var path = rows.IsDBNull(TrackPathColumn) ? null : rows.GetString(TrackPathColumn);
                if (path is not null) AfcClient.ValidatePath(path);

                if (!tracks.TryAdd(rows.GetInt64(TrackSyncIdColumn),
                    new ArtworkReference(ParseId(rows.GetString(TrackTokenColumn)), path)))
                {
                    throw new InvalidDataException("Ambiguous track artwork registration.");
                }
            }
        }

        return catalogue with { ArtworkAvailable = true, ArtworkHighWaterMark = highest, TrackArtwork = tracks };
    }

    private static uint ParseId(string text)
    {
        if (!uint.TryParse(
            text,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var value)
            || value == 0 || value > ArtworkProtocol.MaximumCacheId
            || value.ToString(CultureInfo.InvariantCulture) != text)
        {
            throw new InvalidDataException("Invalid local artwork token.");
        }

        return value;
    }
}
