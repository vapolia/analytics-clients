using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vapolia.Analytics.Client;

// Tz: minutes east of UTC at the instant of the event, or null when nobody knows it.
sealed record Event(string Name, DateTimeOffset Ts, Dictionary<string, PropValue>? Props, int? Tz = null);

/// <summary>
/// What groups events into one request: one batch carries one installation, one device and one
/// context. The context travels as its canonical JSON text rather than a dictionary, so two batches
/// with the same context compare equal and group together.
/// </summary>
sealed record BatchKey(string InstallId, Device Device, string Context = "{}");

sealed record Pending(BatchKey Key, Event Event);

/// <summary>
/// The wire shape of POST /{source}. What describes the device sits on the batch, not on each event.
/// </summary>
sealed class BatchPayload
{
    [JsonPropertyName("installId")] public required string InstallId { get; init; }
    /// <summary>The axis of the source's excludedCountries filter, which is why it stays top-level.</summary>
    [JsonPropertyName("country")] public string? Country { get; init; }
    /// <summary>Whitelisted per source, exactly like an event's props — see IAnalyticsContext.</summary>
    [JsonPropertyName("context")] public Dictionary<string, PropValue>? Context { get; init; }
    [JsonPropertyName("events")] public required List<EventPayload> Events { get; init; }
}

sealed class EventPayload
{
    [JsonPropertyName("name")] public required string Name { get; init; }

    [JsonPropertyName("ts")]
    [JsonConverter(typeof(Utc8601JsonConverter))]
    public required DateTimeOffset Ts { get; init; }

    /// <summary>Minutes east of UTC, apart from ts which stays UTC. Omitted when unknown.</summary>
    [JsonPropertyName("tz")] public int? Tz { get; init; }
    [JsonPropertyName("props")] public Dictionary<string, PropValue>? Props { get; init; }
}

/// <summary>What the spool holds between two runs.</summary>
sealed class SpoolFile
{
    [JsonPropertyName("v")] public int Version { get; init; }
    [JsonPropertyName("items")] public List<SpoolItem> Items { get; init; } = [];
}

sealed class SpoolItem
{
    [JsonPropertyName("id")] public required string InstallId { get; init; }
    [JsonPropertyName("d")] public required Device Device { get; init; }
    [JsonPropertyName("c")] public string? Context { get; init; }
    [JsonPropertyName("n")] public required string Name { get; init; }
    [JsonPropertyName("t")] public required DateTimeOffset Ts { get; init; }
    [JsonPropertyName("p")] public Dictionary<string, PropValue>? Props { get; init; }
    [JsonPropertyName("z")] public int? Tz { get; init; }
}

/// <summary>
/// The serializer's compile-time metadata.
///
/// Source generation rather than reflection: an iOS build is trimmed by default, and the reflection
/// path would be removed from it — the failure would show up at runtime, on a device, not here.
/// See https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/source-generation
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BatchPayload))]
[JsonSerializable(typeof(SpoolFile))]
// The desktop identity store, one flat string map.
[JsonSerializable(typeof(Dictionary<string, string>))]
// The batch context, read back from the canonical text the batch key carries.
[JsonSerializable(typeof(Dictionary<string, PropValue>))]
sealed partial class AnalyticsJsonContext : JsonSerializerContext;

static class BatchEncoder
{
    public static string Encode(BatchKey key, IReadOnlyList<Event> events)
    {
        var payload = new BatchPayload
        {
            InstallId = key.InstallId,
            Country = key.Device.Country,
            Context = key.Context is { Length: > 2 }
                ? JsonSerializer.Deserialize(key.Context, AnalyticsJsonContext.Default.DictionaryStringPropValue)
                : null,
            // Ordered by timestamp: what leaves is in the order it happened, whatever the buffer did.
            Events = events
                .OrderBy(e => e.Ts)
                .Select(e => new EventPayload { Name = e.Name, Ts = e.Ts, Props = e.Props, Tz = e.Tz })
                .ToList(),
        };

        return JsonSerializer.Serialize(payload, AnalyticsJsonContext.Default.BatchPayload);
    }
}
