using System.Text.Json;
using System.Text.Json.Nodes;

namespace OverShell.Core;

/// <summary>
/// JSONC reading for OverShell's own files: comments and trailing commas allowed, errors
/// reported rather than thrown, and a layered merge so a user file only has to name what
/// it changes. Objects merge key by key; arrays and scalars replace; an explicit
/// <c>null</c> removes the key.
/// </summary>
public static class Jsonc
{
    public static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 64,
    };

    public static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    /// <summary>Parses JSONC text into a node tree. Returns null and an error message on malformed input.</summary>
    public static JsonNode? Parse(string text, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            var reader = new Utf8JsonReader(
                System.Text.Encoding.UTF8.GetBytes(text),
                new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 64 });
            return JsonNode.Parse(ref reader, NodeOptions);
        }
        catch (JsonException e)
        {
            error = e.Message;
            return null;
        }
    }

    /// <summary>Reads and parses a JSONC file. A missing file is not an error; a malformed one is.</summary>
    public static JsonNode? ReadFile(string path, out string? error)
    {
        error = null;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return Parse(File.ReadAllText(path), out error);
        }
        catch (IOException e)
        {
            error = e.Message;
            return null;
        }
        catch (UnauthorizedAccessException e)
        {
            error = e.Message;
            return null;
        }
    }

    /// <summary>
    /// Layers <paramref name="overlay"/> onto <paramref name="baseline"/>. Neither input is
    /// modified; the result is a fresh tree.
    /// </summary>
    public static JsonNode? Merge(JsonNode? baseline, JsonNode? overlay)
    {
        if (overlay is null)
        {
            return baseline?.DeepClone();
        }

        if (baseline is not JsonObject baseObject || overlay is not JsonObject overlayObject)
        {
            return overlay.DeepClone();
        }

        var result = (JsonObject)baseObject.DeepClone();
        foreach (var (key, value) in overlayObject)
        {
            if (value is null)
            {
                result.Remove(key);
                continue;
            }

            result[key] = result.TryGetPropertyValue(key, out var existing)
                ? Merge(existing, value)
                : value.DeepClone();
        }

        return result;
    }

    /// <summary>Deserialises a node into a model, reporting instead of throwing on shape errors.</summary>
    public static T? To<T>(JsonNode? node, out string? error)
        where T : class
    {
        error = null;
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.Deserialize<T>(SerializerOptions);
        }
        catch (JsonException e)
        {
            error = e.Message;
            return null;
        }
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, SerializerOptions);
}
