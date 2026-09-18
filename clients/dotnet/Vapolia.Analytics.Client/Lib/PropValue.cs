using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vapolia.Analytics.Client;

/// <summary>
/// A property value. The collector only ever accepts scalars, so this is the whole type.
///
/// It exists so the payload has no <c>object</c> in it: source generation cannot serialize a
/// polymorphic <c>object</c> without reflection, and reflection is what the trimmer removes.
/// </summary>
[JsonConverter(typeof(PropValueJsonConverter))]
readonly struct PropValue : IEquatable<PropValue>
{
    public enum ValueKind : byte { String, Number, Bool }

    public ValueKind Kind { get; }
    public string? StringValue { get; }
    public double NumberValue { get; }
    public bool BoolValue { get; }

    PropValue(ValueKind kind, string? s, double number, bool boolean)
    {
        Kind = kind;
        StringValue = s;
        NumberValue = number;
        BoolValue = boolean;
    }

    public static PropValue From(string value) => new(ValueKind.String, value, 0, false);
    public static PropValue From(double value) => new(ValueKind.Number, null, value, false);
    public static PropValue From(bool value) => new(ValueKind.Bool, null, 0, value);

    /// <summary>What the value looks like to a caller that stored it as an object.</summary>
    public object AsObject() => Kind switch
    {
        ValueKind.String => StringValue!,
        ValueKind.Number => NumberValue,
        _ => BoolValue,
    };

    public bool Equals(PropValue other) => Kind == other.Kind && Kind switch
    {
        ValueKind.String => StringValue == other.StringValue,
        ValueKind.Number => NumberValue.Equals(other.NumberValue),
        _ => BoolValue == other.BoolValue,
    };

    public override bool Equals(object? obj) => obj is PropValue other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Kind, StringValue, NumberValue, BoolValue);

    public override string ToString() => AsObject().ToString() ?? "";
}

/// <summary>
/// Written by hand, which keeps it trim-safe: a converter is code, not reflection, and the source
/// generator uses it as is.
/// </summary>
sealed class PropValueJsonConverter : JsonConverter<PropValue>
{
    public override PropValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => PropValue.From(reader.GetString()!),
            JsonTokenType.Number => PropValue.From(reader.GetDouble()),
            JsonTokenType.True => PropValue.From(true),
            JsonTokenType.False => PropValue.From(false),
            _ => throw new JsonException($"a property value cannot be {reader.TokenType}"),
        };

    public override void Write(Utf8JsonWriter writer, PropValue value, JsonSerializerOptions options)
    {
        switch (value.Kind)
        {
            case PropValue.ValueKind.String:
                writer.WriteStringValue(value.StringValue);
                break;
            case PropValue.ValueKind.Bool:
                writer.WriteBooleanValue(value.BoolValue);
                break;
            default:
                var number = value.NumberValue;
                // Whole values are written without a fractional part, so `moves` reads as 34, not 34.0.
                if (number == Math.Floor(number) && Math.Abs(number) < 1e15)
                    writer.WriteNumberValue((long)number);
                else
                    writer.WriteNumberValue(number);
                break;
        }
    }
}

/// <summary>
/// Timestamps go out as UTC with a trailing Z, the shape every other client of this collector sends.
/// The default converter would write the offset instead, which parses the same but reads differently
/// in a payload someone is debugging.
/// </summary>
sealed class Utc8601JsonConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetDateTimeOffset();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            System.Globalization.CultureInfo.InvariantCulture));
}
