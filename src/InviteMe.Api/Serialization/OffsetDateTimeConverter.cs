using System.Text.Json;
using System.Text.Json.Serialization;

namespace InviteMe.Api.Serialization;

// Prevent a browser's offset-free local date from being interpreted in the server timezone.
internal sealed class OffsetDateTimeConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        var hasOffset = text is not null && (text.EndsWith('Z') || text.Length >= 6 && text[^6] is '+' or '-' && text[^3] == ':');
        if (!hasOffset || !reader.TryGetDateTimeOffset(out var value)) throw new JsonException("Timestamp must include Z or an explicit offset.");
        return value.ToUniversalTime();
    }
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToUniversalTime());
}
