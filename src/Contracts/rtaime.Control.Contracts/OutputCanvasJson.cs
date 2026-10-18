// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace rtaime.Control.Contracts;

public static class OutputCanvasJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    public static T Read<T>(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > OutputCanvasContract.MaximumJsonBytes)
            throw new InvalidDataException("Canvas request exceeds its byte limit.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicates(document.RootElement);
        return JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("Canvas payload is required.");
    }

    public static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate canvas JSON property.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }
}
