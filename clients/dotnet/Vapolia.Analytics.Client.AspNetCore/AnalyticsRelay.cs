using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Vapolia.Analytics.Client;

/// <summary>
/// Validates or transforms one prop of a relayed event. Returns the value to keep, or null to drop the
/// prop. The event itself is kept.
/// </summary>
public delegate ValueTask<object?> RelayPropValidator(RelayProp prop);

/// <summary>One prop of a relayed event, as the browser sent it.</summary>
/// <param name="Event">The event name.</param>
/// <param name="Key">The prop key.</param>
/// <param name="Value">A string, a long, a double or a bool.</param>
/// <param name="Props">All props of the event before validation, fixed props included.</param>
/// <param name="Context">The relay request. <c>Context.RequestServices</c> resolves scoped services.</param>
public readonly record struct RelayProp(string Event, string Key, object Value, IReadOnlyDictionary<string, object?> Props, HttpContext Context);

/// <summary>The events and props a relay accepts. Anything else is dropped silently.</summary>
public sealed class AnalyticsRelayOptions
{
    internal Dictionary<string, RelayEvent> Events { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, RelayPropValidator> Validators { get; } = new(StringComparer.Ordinal);

    /// <summary>Events beyond this count in one request are dropped.</summary>
    public int MaxEventsPerRequest { get; set; } = 20;

    /// <summary>A request body larger than this is dropped whole.</summary>
    public int MaxBodyBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// Accepts the event <paramref name="name"/> with the props <paramref name="keys"/>.
    /// </summary>
    public RelayEvent Event(string name, params string[] keys)
    {
        var relayEvent = new RelayEvent(keys);
        Events[name] = relayEvent;
        return relayEvent;
    }

    /// <summary>
    /// Validates the prop <paramref name="key"/> on every event that accepts it. A key without a
    /// validator is relayed as sent.
    /// </summary>
    public AnalyticsRelayOptions Prop(string key, RelayPropValidator validator)
    {
        Validators[key] = validator;
        return this;
    }
}

/// <summary>One accepted event: its allowed props and the props the server sets itself.</summary>
public sealed class RelayEvent
{
    internal RelayEvent(string[] keys) => Keys = keys;

    internal string[] Keys { get; }
    internal Dictionary<string, object> Fixed { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Sets <paramref name="key"/> to <paramref name="value"/> on this event, whatever the browser sent.
    /// It is not validated, and validators see it in <see cref="RelayProp.Props"/>.
    /// </summary>
    public RelayEvent With(string key, object value)
    {
        Fixed[key] = value;
        return this;
    }
}

/// <summary>Maps the endpoints a browser calls when the server emits the events for it.</summary>
public static class AnalyticsRelayExtensions
{
    /// <summary>
    /// Maps three endpoints under <paramref name="prefix"/>:
    /// <list type="bullet">
    /// <item><c>GET state</c> returns <c>{"requiresPriorConsent":bool,"consentAnswer":bool|null}</c>.</item>
    /// <item><c>POST consent</c> with <c>{"accepted":bool}</c> sets <see cref="IInstallContext.IsOptedOut"/>. Answers 204, or 400 on an unreadable body.</item>
    /// <item><c>POST events</c> with <c>[{"name":"…","props":{…}}]</c>, or one such object, tracks the accepted events. Always answers 204 with no body. Any content type is read as JSON, so <c>navigator.sendBeacon</c> works with a plain string.</item>
    /// </list>
    /// <code>
    /// app.MapAnalyticsRelay("/api/web/analytics", o =>
    /// {
    ///     o.Prop("duration_s", p => new(p.Value is double or long ? Math.Clamp(Convert.ToDouble(p.Value), 0, 86400) : null));
    ///     o.Event("content_viewed", "content_type", "content_id", "duration_s");
    ///     o.Event("route_shared", "content_id").With("content_type", "route");
    /// }).RequireRateLimiting("analytics");
    /// </code>
    /// </summary>
    public static RouteGroupBuilder MapAnalyticsRelay(this IEndpointRouteBuilder endpoints, string prefix, Action<AnalyticsRelayOptions> configure)
    {
        var options = new AnalyticsRelayOptions();
        configure(options);

        var group = endpoints.MapGroup(prefix);
        group.MapGet("state", (RequestDelegate)State);
        group.MapPost("consent", (RequestDelegate)(context => Consent(context, options)));
        group.MapPost("events", (RequestDelegate)(context => Events(context, options)));
        return group;
    }

    internal static async Task State(HttpContext context)
    {
        var install = context.RequestServices.GetRequiredService<IInstallContext>();
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";

        await using var writer = new Utf8JsonWriter(context.Response.Body);
        writer.WriteStartObject();
        writer.WriteBoolean("requiresPriorConsent", install.RequiresPriorConsent);
        if (install.ConsentAnswer is { } answer)
            writer.WriteBoolean("consentAnswer", answer);
        else
            writer.WriteNull("consentAnswer");
        writer.WriteEndObject();
    }

    internal static async Task Consent(HttpContext context, AnalyticsRelayOptions options)
    {
        using var body = await ReadBody(context, options.MaxBodyBytes);
        if (body?.RootElement is not { ValueKind: JsonValueKind.Object } root
            || !root.TryGetProperty("accepted", out var accepted)
            || accepted.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        context.RequestServices.GetRequiredService<IInstallContext>().IsOptedOut = !accepted.GetBoolean();
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    internal static async Task Events(HttpContext context, AnalyticsRelayOptions options)
    {
        // Set first: a beacon reads no answer, and the cookie must leave with the headers.
        context.Response.StatusCode = StatusCodes.Status204NoContent;

        using var body = await ReadBody(context, options.MaxBodyBytes);
        if (body is null)
            return;

        var items = body.RootElement.ValueKind switch
        {
            JsonValueKind.Array => body.RootElement.EnumerateArray().Take(options.MaxEventsPerRequest).ToList(),
            JsonValueKind.Object => [body.RootElement],
            _ => [],
        };

        var analytics = context.RequestServices.GetRequiredService<IAnalytics>();
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("name", out var nameElement)
                || nameElement.GetString() is not { } name
                || !options.Events.TryGetValue(name, out var relayEvent))
                continue;

            var sent = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (item.TryGetProperty("props", out var propsElement) && propsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in propsElement.EnumerateObject())
                {
                    if (relayEvent.Keys.Contains(prop.Name, StringComparer.Ordinal) && Scalar(prop.Value) is { } value)
                        sent[prop.Name] = value;
                }
            }

            foreach (var (key, value) in relayEvent.Fixed)
                sent[key] = value;

            var props = new Dictionary<string, object?>(sent.Count, StringComparer.Ordinal);
            foreach (var (key, value) in sent)
            {
                if (relayEvent.Fixed.ContainsKey(key) || !options.Validators.TryGetValue(key, out var validate))
                {
                    props[key] = value;
                    continue;
                }

                if (await validate(new RelayProp(name, key, value!, sent, context)) is { } kept)
                    props[key] = kept;
            }

            analytics.Track(name, props);
        }
    }

    static object? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var l) ? l : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    /// <summary>The body as JSON, or null when it is too large or unreadable.</summary>
    static async Task<JsonDocument?> ReadBody(HttpContext context, int maxBytes)
    {
        if (context.Request.ContentLength > maxBytes)
            return null;

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        try
        {
            return JsonDocument.Parse(buffer.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
