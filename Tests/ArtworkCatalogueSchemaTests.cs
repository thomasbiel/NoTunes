using NUnit.Framework;

namespace NoTunes.Tests;

[TestFixture]
public sealed class ArtworkCatalogueSchemaTests
{
    [Test]
    public async Task Uses_The_Available_Token_From_The_Device_Best_Artwork_Schema([Values] bool available)
    {
        using var fixture = new CatalogueFixture();
        fixture.AddArtworkSchema();
        fixture.Execute("""
            INSERT INTO artwork(artwork_token,artwork_source_type,relative_path,artwork_type,artwork_variant_type)
                VALUES('101',300,'aa/cover.jpg',1,0);
            INSERT INTO artwork_token(entity_pid,artwork_token,artwork_source_type,artwork_type,entity_type,
                artwork_variant_type) VALUES(1,'101',300,1,0,0);
            INSERT INTO best_artwork_token(entity_pid,entity_type,artwork_type,available_artwork_token,
                fetchable_artwork_token,fetchable_artwork_source_type,artwork_variant_type)
                VALUES(1,0,1,'100','101',300,0);
            """);

        if (available)
        {
            fixture.Execute("UPDATE best_artwork_token SET available_artwork_token='101';");
        }

        var catalogue = await fixture.ReadAsync();
        Assert.That(catalogue.ArtworkAvailable, Is.True);
        Assert.That(catalogue.TrackArtwork[101].CacheId, Is.EqualTo(101));
        Assert.That(catalogue.TrackArtwork[101].Path, Is.EqualTo(available ? "aa/cover.jpg" : null));
    }
}
