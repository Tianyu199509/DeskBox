using System.Text.Json.Serialization;

namespace DeskBox.Services.Lyrics;

public sealed class LrclibTrackDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("trackName")]
    public string? TrackName { get; set; }

    [JsonPropertyName("artistName")]
    public string? ArtistName { get; set; }

    [JsonPropertyName("albumName")]
    public string? AlbumName { get; set; }

    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    [JsonPropertyName("instrumental")]
    public bool Instrumental { get; set; }

    [JsonPropertyName("plainLyrics")]
    public string? PlainLyrics { get; set; }

    [JsonPropertyName("syncedLyrics")]
    public string? SyncedLyrics { get; set; }
}

public sealed class CachedLyricsEntry
{
    public string CacheKey { get; set; } = string.Empty;

    public string Status { get; set; } = CachedLyricsStatus.NotFound;

    public string? SyncedLyrics { get; set; }

    public double DurationSeconds { get; set; }

    public int UserOffsetMs { get; set; }

    public long UpdatedUtcTicks { get; set; }
}

public static class CachedLyricsStatus
{
    public const string Synced = "Synced";
    public const string Instrumental = "Instrumental";
    public const string NotFound = "NotFound";
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(LrclibTrackDto[]))]
[JsonSerializable(typeof(CachedLyricsEntry))]
internal sealed partial class LyricsJsonContext : JsonSerializerContext
{
}
