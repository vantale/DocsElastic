static JsonElement GetArrayOrThrow(JsonElement root, string context)
{
    if (root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array) return v;
    if (root.TryGetProperty("d", out var d) && d.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array) return r;
    throw new InvalidOperationException($"Expected an array in {context} response.");
}

var arr = GetArrayOrThrow(listsDoc.RootElement, "lists");
