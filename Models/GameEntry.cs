using System.Text.Json.Serialization;

namespace PSPriceNotification.Models;

public class GameEntry
{
    public string Name { get; set; } = "";

    [JsonPropertyName("concept_id")]
    public string? ConceptId { get; set; }

    [JsonPropertyName("product_id")]
    public string? ProductId { get; set; }

    public List<string>? Countries { get; set; }

    public string Note { get; set; } = "";

    [JsonIgnore]
    public string? Id => ConceptId ?? ProductId;

    [JsonIgnore]
    public string IdType => ConceptId != null ? "concept" : "product";
}

public class FavoritesFile
{
    public List<GameEntry> Games { get; set; } = [];
}
