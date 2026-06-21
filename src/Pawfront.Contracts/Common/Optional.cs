using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pawfront.Contracts.Common;

/// <summary>
/// A field that may or may not have been supplied on a PATCH request body.
/// Lets a partial-update endpoint tell "the client omitted this field" (leave
/// it unchanged) apart from "the client explicitly sent null" (clear it) —
/// the two are indistinguishable with a plain nullable property.
///
/// Because of the attached <see cref="OptionalJsonConverterFactory"/>,
/// System.Text.Json only invokes the converter for properties that are
/// actually present in the JSON, so an absent property deserialises to
/// <c>default(Optional&lt;T&gt;)</c> with <see cref="IsSet"/> = false. A present
/// property (even <c>null</c>) yields <see cref="IsSet"/> = true.
/// </summary>
[JsonConverter(typeof(OptionalJsonConverterFactory))]
public readonly struct Optional<T>
{
    public Optional(T value)
    {
        Value = value;
        IsSet = true;
    }

    /// <summary>True when the field was present in the request body.</summary>
    public bool IsSet { get; }

    /// <summary>The supplied value. Meaningful only when <see cref="IsSet"/> is true.</summary>
    public T Value { get; }

    /// <summary>
    /// Returns the supplied value when the field was present, otherwise the
    /// fallback (the current persisted value) — the core PATCH merge step.
    /// </summary>
    public T Or(T fallback) => IsSet ? Value : fallback;
}

/// <summary>
/// Produces a typed <see cref="OptionalJsonConverter{T}"/> for any
/// <see cref="Optional{T}"/>.
/// </summary>
public sealed class OptionalJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert.IsGenericType
           && typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var valueType = typeToConvert.GetGenericArguments()[0];
        var converterType = typeof(OptionalJsonConverter<>).MakeGenericType(valueType);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}

internal sealed class OptionalJsonConverter<T> : JsonConverter<Optional<T>>
{
    // Only called when the property is present in the payload, so reaching here
    // always means IsSet = true. The inner value is deserialised with the same
    // options (naming policy, nested converters) as the rest of the body.
    public override Optional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(JsonSerializer.Deserialize<T>(ref reader, options)!);

    public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
    {
        // Optional<T> is inbound-only (PATCH bodies), but provide a sane writer
        // so it never throws if accidentally serialised.
        if (value.IsSet)
        {
            JsonSerializer.Serialize(writer, value.Value, options);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
