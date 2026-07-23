using System.Security.Cryptography;
using System.Text.Json;

namespace ONI_Together.HeadlessTests;

internal static class SyncCanonicalJson
{
    public static string Sha256(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            Write(writer, value);
        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(writer, value);
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                    Write(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static void WriteObject(Utf8JsonWriter writer, JsonElement value)
    {
        writer.WriteStartObject();
        foreach (JsonProperty property in value.EnumerateObject()
                     .OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            writer.WritePropertyName(property.Name);
            Write(writer, property.Value);
        }
        writer.WriteEndObject();
    }
}
