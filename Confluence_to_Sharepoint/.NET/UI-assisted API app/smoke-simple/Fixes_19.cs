// Already added by you:
static JsonElement GetArrayOrThrow(JsonElement root, string context)
{
    if (root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array) return v;
    if (root.TryGetProperty("d", out var d) && d.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array) return r;
    throw new InvalidOperationException($"Expected an array in {context} response.");
}

static string ODataEscape(string s) => s.Replace("'", "''");

// Find a page by file name or title across ANY Site Pages/Pages library (template 119 or 850)
static async Task<string> ResolvePagePathAcrossLibrariesAsync(HttpClient http, Uri baseUri, string hint)
{
    // 1) Enumerate candidate libraries by BaseTemplate: 119 (Site Pages), 850 (Publishing Pages)
    var listsUrl = new Uri(baseUri,
        "/_api/web/lists?$select=Id,Title,BaseTemplate,RootFolder/ServerRelativeUrl&$expand=RootFolder&$filter=(BaseTemplate eq 119) or (BaseTemplate eq 850)");
    using var lr = await http.GetAsync(listsUrl);
    Console.WriteLine($"[Lists by template 119/850] HTTP {(int)lr.StatusCode} {lr.ReasonPhrase}");
    lr.EnsureSuccessStatusCode();

    var listsJson = await lr.Content.ReadAsStringAsync();
    using var listsDoc = JsonDocument.Parse(listsJson);
    var arr = GetArrayOrThrow(listsDoc.RootElement, "lists");

    var isFile = hint.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase);
    var fileLeaf = isFile ? hint : (hint.Replace(' ', '-') + ".aspx");

    for (int i = 0; i < arr.GetArrayLength(); i++)
    {
        var el = arr[i];
        var id = el.GetProperty("Id").GetString();
        var title = el.TryGetProperty("Title", out var t) ? t.GetString() : "(no title)";
        var rootFolder = el.GetProperty("RootFolder").GetProperty("ServerRelativeUrl").GetString();
        var baseTemplate = el.GetProperty("BaseTemplate").GetInt32();
        Console.WriteLine($"[List] {title} (template {baseTemplate}) root='{rootFolder}'");

        // 2a) Try by FileLeafRef
        var byLeaf = new Uri(baseUri,
            $"/_api/web/lists(guid'{id}')/items?$select=Id,FileLeafRef,FileRef,Title&$filter=FileLeafRef eq '{ODataEscape(fileLeaf)}'&$top=1");
        using (var r1 = await http.GetAsync(byLeaf))
        {
            Console.WriteLine($"  [Query by leaf] {fileLeaf} → {(int)r1.StatusCode}");
            if (r1.IsSuccessStatusCode)
            {
                var j = await r1.Content.ReadAsStringAsync();
                using var d1 = JsonDocument.Parse(j);
                var items = GetArrayOrThrow(d1.RootElement, "items-by-leaf");
                if (items.GetArrayLength() > 0)
                    return NormalizeServerRelativePath(items[0].GetProperty("FileRef").GetString()!);
            }
        }

        // 2b) Try by Title (only if hint looked like a title)
        if (!isFile)
        {
            var byTitle = new Uri(baseUri,
                $"/_api/web/lists(guid'{id}')/items?$select=Id,FileLeafRef,FileRef,Title&$filter=Title eq '{ODataEscape(hint)}'&$orderby=Modified desc&$top=1");
            using var r2 = await http.GetAsync(byTitle);
            Console.WriteLine($"  [Query by title] {hint} → {(int)r2.StatusCode}");
            if (r2.IsSuccessStatusCode)
            {
                var j = await r2.Content.ReadAsStringAsync();
                using var d2 = JsonDocument.Parse(j);
                var items = GetArrayOrThrow(d2.RootElement, "items-by-title");
                if (items.GetArrayLength() > 0)
                    return NormalizeServerRelativePath(items[0].GetProperty("FileRef").GetString()!);
            }
        }
    }

    throw new InvalidOperationException(
        $"Could not find a page matching '{hint}' in any Site Pages/Pages library on {baseUri}.");
}
