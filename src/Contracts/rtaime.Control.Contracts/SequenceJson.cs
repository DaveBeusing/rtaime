// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace rtaime.Control.Contracts;

public static class SequenceJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32
    };
    public static T Read<T>(string json, int maximumBytes = SequenceContract.MaximumJsonBytes)
    {
        if (Encoding.UTF8.GetByteCount(json) > maximumBytes) throw new InvalidDataException("Sequence JSON exceeds its finite byte limit.");
        using var parsed = JsonDocument.Parse(json,new JsonDocumentOptions { MaxDepth=32 });
        Check(parsed.RootElement);
        return JsonSerializer.Deserialize<T>(json,Options) ?? throw new InvalidDataException("Sequence payload is required.");
    }
    private static void Check(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate sequence JSON property.");
                Check(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Check(item);
    }
}
