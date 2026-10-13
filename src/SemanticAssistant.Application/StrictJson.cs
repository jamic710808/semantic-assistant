using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SemanticAssistant.Application;

public static class StrictJson
{
    public static T Parse<T>(string json, int maximumBytes = 32_000, int maximumDepth = 16)
    {
        if (Encoding.UTF8.GetByteCount(json) > maximumBytes) throw new InvalidDataException("JSON 超過大小上限。");
        using var document = JsonDocument.Parse(json, new() { MaxDepth = maximumDepth });
        RejectDuplicates(document.RootElement);
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
        {
            MaxDepth = maximumDepth,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
        }) ?? throw new InvalidDataException("JSON 不可為 null。");
    }
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("JSON 欄位重複。");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }
}
