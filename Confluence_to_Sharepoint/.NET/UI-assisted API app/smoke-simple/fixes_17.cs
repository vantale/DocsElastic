// Get the "value" array for both OData shapes (nometadata vs verbose)
static JsonElement? TryGetArray(JsonElement root)
{
    if (root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array) return v;
    if (root.TryGetProperty("d", out var d) && d.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array) return r;
    return null;
}

// Normalize server-relative paths (handles %2F, extra slashes, stray ';')
static string NormalizeServerRelativePath(string raw)
{
    var p = Uri.UnescapeDataString(raw ?? string.Empty).Trim();
    if (!p.StartsWith("/")) p = "/" + p;
    while (p.EndsWith(";") || p.EndsWith(" ")) p = p[..^1];
    p = "/" + string.Join("/", p.Split('/', StringSplitOptions.RemoveEmptyEntries));
    return p;
}

// Build endpoints with ...ByServerRelativeUrl (escape only single quotes)
static (Uri fieldsUrl, Uri publishUrl, Uri probeUrl) BuildFileEndpoints(Uri baseUri, string serverRelativePath)
{
    var safe = serverRelativePath.Replace("'", "''");
    var fields  = new Uri(baseUri, $"/_api/web/GetFileByServerRelativeUrl('{safe}')/ListItemAllFields");
    var publish = new Uri(baseUri, $"/_api/web/GetFileByServerRelativeUrl('{safe}')/Publish(StringParameter='Smoke update')");
    var probe   = new Uri(baseUri, $"/_api/web/GetFileByServerRelativeUrl('{safe}')?$select=Name,Exists,ServerRelativeUrl");
    return (fields, publish, probe);
}

// Find a page by file name or title across ANY Site Pages/Pages library (template 119 or 850)
static async Task<string> ResolvePagePathAcrossLibrariesAsync(HttpClient http, Uri baseUri, string hint)
{
    // 1) Enumerate candidate libraries by BaseTemplate: 119 (Wiki/Site Pages), 850 (Publishing "Pages")
    var listsUrl = new Uri(baseUri,
        "/_api/web/lists?$select=Id,Title,BaseTemplate,RootFolder/ServerRelativeUrl&$expand=RootFolder&$filter=(BaseTemplate eq 119) or (BaseTemplate eq 850)");
    using var lr = await http.GetAsync(listsUrl);
    Console.WriteLine($"[Lists by template 119/850] HTTP {(int)lr.StatusCode} {lr.ReasonPhrase}");
    lr.EnsureSuccessStatusCode();

    var listsJson = await lr.Content.ReadAsStringAsync();
    using var listsDoc = JsonDocument.Parse(listsJson);
    var arr = TryGetArray(listsDoc.RootElement) ?? throw new InvalidOperationException("No lists returned.");

    // Prepare search keys
    var isFile = hint.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase);
    var fileLeaf = isFile ? hint : (hint.Replace(' ', '-') + ".aspx");

    // 2) Try each candidate list
    for (int i = 0; i < arr.Value.GetArrayLength(); i++)
    {
        var el = arr.Value[i];
        var id = el.GetProperty("Id").GetString();
        var title = el.TryGetProperty("Title", out var t) ? t.GetString() : "(no title)";
        var rootFolder = el.GetProperty("RootFolder").GetProperty("ServerRelativeUrl").GetString();
        Console.WriteLine($"[List] {title} (template {el.GetProperty("BaseTemplate").GetInt32()}) root='{rootFolder}'");

        // 2a) Try by FileLeafRef (exact file name)
        var byLeaf = new Uri(baseUri,
            $"/_api/web/lists(guid'{id}')/items?$select=Id,FileLeafRef,FileRef,Title&$filter=FileLeafRef eq '{fileLeaf.Replace("'", "''")}'&$top=1");
        using (var r1 = await http.GetAsync(byLeaf))
        {
            Console.WriteLine($"  [Query by leaf] {fileLeaf} → {(int)r1.StatusCode}");
            if (r1.IsSuccessStatusCode)
            {
                var j = await r1.Content.ReadAsStringAsync();
                using var d = JsonDocument.Parse(j);
                var items = TryGetArray(d);
                if (items is { ValueKind: JsonValueKind.Array } && items.Value.GetArrayLength() > 0)
                {
                    var fileRef = items.Value[0].GetProperty("FileRef").GetString()!;
                    return NormalizeServerRelativePath(fileRef);
                }
            }
        }

        // 2b) If hint looked like a title, try by Title
        if (!isFile)
        {
            var byTitle = new Uri(baseUri,
                $"/_api/web/lists(guid'{id}')/items?$select=Id,FileLeafRef,FileRef,Title&$filter=Title eq '{hint.Replace("'", "''")}'&$orderby=Modified desc&$top=1");
            using var r2 = await http.GetAsync(byTitle);
            Console.WriteLine($"  [Query by title] {hint} → {(int)r2.StatusCode}");
            if (r2.IsSuccessStatusCode)
            {
                var j = await r2.Content.ReadAsStringAsync();
                using var d = JsonDocument.Parse(j);
                var items = TryGetArray(d);
                if (items is { ValueKind: JsonValueKind.Array } && items.Value.GetArrayLength() > 0)
                {
                    var fileRef = items.Value[0].GetProperty("FileRef").GetString()!;
                    return NormalizeServerRelativePath(fileRef);
                }
            }
        }
    }

    throw new InvalidOperationException(
        $"Could not find a page matching '{hint}' in any Site Pages/Pages library on {baseUri}.");
}


// 3) MERGE CanvasContent1 (replace canvas) + ensure modern layout
var normalizedPath = NormalizeServerRelativePath(pageServerRelativePath);
var (fieldsUrl, publishUrl, probeUrl) = BuildFileEndpoints(baseUri, normalizedPath);

// Log the full probe URL to see the exact site we’re hitting
Console.WriteLine($"[Probe URL] {probeUrl}");

using (var probe = await http.GetAsync(probeUrl))
{
    Console.WriteLine($"[Probe file exists] {normalizedPath} → HTTP {(int)probe.StatusCode} {probe.ReasonPhrase}");
    if (probe.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        // Auto-resolve across libraries (handles localized/renamed "Site Pages" / "Pages")
        var hint = normalizedPath.Split('/').Last(); // e.g., "First-Test-Page.aspx"
        normalizedPath = await ResolvePagePathAcrossLibrariesAsync(http, baseUri, hint);
        Console.WriteLine($"[Resolved path] {normalizedPath}");
        (fieldsUrl, publishUrl, probeUrl) = BuildFileEndpoints(baseUri, normalizedPath);

        using var probe2 = await http.GetAsync(probeUrl);
        Console.WriteLine($"[Probe#2 file exists] {normalizedPath} → HTTP {(int)probe2.StatusCode} {probe2.ReasonPhrase}");
        probe2.EnsureSuccessStatusCode();
    }
    else
    {
        probe.EnsureSuccessStatusCode();
    }
}


