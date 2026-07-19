using System.Text;
using System.Text.Json;

namespace BaseClean.Web.Auditing;

public sealed class SensitiveDataRedactor
{
    private static readonly string[] Sensitive = ["password", "token", "secret", "pwd"];
    private const int MaxLength = 8192;

    public string? SerializeAndRedact(IDictionary<string, object?> args)
    {
        if (args is null || args.Count == 0) return null;

        var filtered = args
            .Where(kv => kv.Value is not IFormFile
                      && kv.Value is not IFormFileCollection
                      && kv.Value is not IEnumerable<IFormFile>)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        if (filtered.Count == 0) return null;

        try
        {
            using var doc = JsonSerializer.SerializeToDocument(filtered);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteRedacted(doc.RootElement, writer, propertyName: null);
            }
            var result = Encoding.UTF8.GetString(stream.ToArray());
            return result.Length > MaxLength ? result[..MaxLength] : result;
        }
        catch
        {
            return null; // serialization must never break the request
        }
    }

    private static void WriteRedacted(JsonElement element, Utf8JsonWriter writer, string? propertyName)
    {
        if (propertyName is not null && IsSensitive(propertyName))
        {
            writer.WriteStringValue("***");
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject())
                {
                    writer.WritePropertyName(prop.Name);
                    WriteRedacted(prop.Value, writer, prop.Name);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteRedacted(item, writer, null);
                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsSensitive(string name)
        => Sensitive.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase));
}
