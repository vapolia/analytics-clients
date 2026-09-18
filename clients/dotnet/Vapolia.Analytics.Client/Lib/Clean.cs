using System.Text;

namespace Vapolia.Analytics.Client;

/// <summary>
/// Limits mirrored from the collector's EventSanitizer. Applying them here does not make the server
/// trust the client — it re-applies all of them — it only keeps the wire free of payloads that would
/// be dropped on arrival.
/// </summary>
static class Clean
{
    public const int MaxEventsPerBatch = 100;
    public const int MaxPropsPerEvent = 12;

    /// <summary>Batch-context keys kept, mirroring the collector's own ceiling.</summary>
    public const int MaxContextKeys = 12;
    public const int MaxValueLength = 64;
    public const int MaxAgeBucketLength = 12;

    /// <summary>How far back the collector accepts a timestamp. Older events are dropped, not sent.</summary>
    public static readonly TimeSpan MaxEventAge = TimeSpan.FromDays(7);

    /// <summary>
    /// Trims, caps the length, strips control characters, and turns blank into null. The control pass
    /// is not cosmetic: Postgres rejects U+0000 in text and jsonb.
    /// </summary>
    public static string? Text(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            trimmed = trimmed[..maxLength];

        StringBuilder? cleaned = null;
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (!char.IsControl(c))
            {
                cleaned?.Append(c);
                continue;
            }

            cleaned ??= new StringBuilder(trimmed.Length).Append(trimmed, 0, i);
        }

        var result = cleaned?.ToString().Trim() ?? trimmed;
        return result.Length == 0 ? null : result;
    }

    /// <summary>
    /// Exactly two ASCII letters, or nothing: truncating would turn "GERMANY" into "GE", which is
    /// Georgia.
    /// </summary>
    public static string? Country(string? value)
    {
        var trimmed = value?.Trim();
        if (trimmed is not { Length: 2 })
            return null;

        return char.IsAsciiLetter(trimmed[0]) && char.IsAsciiLetter(trimmed[1])
            ? trimmed.ToUpperInvariant()
            : null;
    }

    /// <summary>The canonical UUID form only, and never the nil UUID.</summary>
    public static string? InstallId(string? value)
    {
        if (!Guid.TryParseExact(value?.Trim(), "D", out var parsed) || parsed == Guid.Empty)
            return null;

        return parsed.ToString("D");
    }

    /// <summary>Scalars only, capped in count and in length.</summary>
    public static Dictionary<string, PropValue>? Props(IReadOnlyDictionary<string, object?>? props, int maxKeys = MaxPropsPerEvent)
    {
        if (props is null || props.Count == 0)
            return null;

        var kept = new Dictionary<string, PropValue>(StringComparer.Ordinal);
        // Sorted so that what survives the cap does not depend on the dictionary's order — and, for a
        // batch context, so the same context always serialises identically and groups as one batch.
        foreach (var key in props.Keys.Order(StringComparer.Ordinal))
        {
            if (kept.Count == maxKeys)
                break;

            switch (props[key])
            {
                case string s when Text(s, MaxValueLength) is { } cleaned:
                    kept[key] = PropValue.From(cleaned);
                    break;
                case bool b:
                    kept[key] = PropValue.From(b);
                    break;
                // Before the numeric case, which would otherwise store the ordinal of an int-backed
                // enum: the name is what a query filters on, never a number whose meaning changes the
                // day someone reorders the enum.
                case Enum e when Text(e.ToString(), MaxValueLength) is { } named:
                    kept[key] = PropValue.From(named);
                    break;
                // NaN and the infinities are not representable in jsonb, and one of them would fail
                // the insert for the whole batch.
                case IConvertible c and (sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal):
                    var number = c.ToDouble(null);
                    if (double.IsFinite(number))
                        kept[key] = PropValue.From(number);
                    break;
            }
        }

        return kept.Count == 0 ? null : kept;
    }
}
