using System.Text.Json;

namespace OverShell.Config;

/// <summary>Shared JSON plumbing. Windows Terminal writes JSONC, so comments and trailing commas are legal.</summary>
internal static class Json
{
    public static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 64,
    };

    public static JsonElement? Prop(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value
            : null;

    public static string? Str(this JsonElement element, string name)
    {
        var prop = element.Prop(name);
        return prop is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    }

    public static bool? Bool(this JsonElement element, string name) => element.Prop(name) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        _ => null,
    };

    public static double? Num(this JsonElement element, string name)
    {
        var prop = element.Prop(name);
        return prop is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var d) ? d : null;
    }

    public static IEnumerable<JsonElement> Array(this JsonElement element, string name)
    {
        var prop = element.Prop(name);
        return prop is { ValueKind: JsonValueKind.Array } value ? value.EnumerateArray() : [];
    }

    /// <summary>Normalises <c>{GUID}</c> / <c>GUID</c> / mixed case into a single comparable form.</summary>
    public static string? NormalizeGuid(string? raw) =>
        Guid.TryParse(raw?.Trim('{', '}'), out var guid) ? guid.ToString("D") : null;
}
