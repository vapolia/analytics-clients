using System.Buffers;
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

    /// <summary>How far back the collector accepts a timestamp. Older events are dropped, not sent.</summary>
    public static readonly TimeSpan MaxEventAge = TimeSpan.FromDays(7);

    private static readonly SearchValues<char> ControlChars = SearchValues.Create(
        "\0\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u0009\u000A\u000B\u000C\u000D\u000E\u000F" +
        "\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001A\u001B\u001C\u001D\u001E\u001F" +
        "\u007F\u0080\u0081\u0082\u0083\u0084\u0085\u0086\u0087\u0088\u0089\u008A\u008B\u008C\u008D\u008E" +
        "\u008F\u0090\u0091\u0092\u0093\u0094\u0095\u0096\u0097\u0098\u0099\u009A\u009B\u009C\u009D\u009E\u009F");

    /// <summary>
    /// Trims, caps the length, strips control characters
    /// </summary>
    public static ReadOnlySpan<char> Text(ReadOnlySpan<char> value, int maxLength)
    {
        value = value.Trim();
        if (value.IsEmpty || maxLength <= 0) 
            return ReadOnlySpan<char>.Empty;

        if (value.Length > maxLength)
        {
            // Never half an emoji: a lone surrogate reaches the collector as U+FFFD.
            var cut = char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;
            value = value[..cut];
        }
        
        var index = value.IndexOfAny(ControlChars);
        if (index < 0) 
            return value.ToString();
        
        var sb = new StringBuilder(value.Length);
        sb.Append(value[..index]);
        for (var i = index + 1; i < value.Length; i++)
        {
            if (!ControlChars.Contains(value[i])) 
                sb.Append(value[i]);
        }

        return sb.ToString().AsSpan().Trim();
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

    /// <summary>Scalars only, capped in count and in length. Keys are cleaned like the values.</summary>
    public static Dictionary<string, PropValue>? Props(IReadOnlyDictionary<string, object?>? props, int maxKeys = MaxPropsPerEvent)
    {
        if (props is null || props.Count == 0)
            return null;

        var kept = new Dictionary<string, PropValue>(StringComparer.Ordinal);
        foreach (var (rawKey, value) in props)
        {
            if (kept.Count == maxKeys)
                break;

            var key = Text(rawKey, MaxValueLength).ToString();
            if (key.Length == 0)
                continue;

            switch (value)
            {
                case string s when Text(s, MaxValueLength) is { IsEmpty: false } cleaned:
                    kept[key] = PropValue.From(cleaned.ToString());
                    break;
                case Enum e when Text(e.ToString(), MaxValueLength) is { IsEmpty: false } named:
                    kept[key] = PropValue.From(named.ToString());
                    break;
                case bool b:
                    kept[key] = PropValue.From(b);
                    break;
                // NaN and the infinities are not representable in jsonb
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
