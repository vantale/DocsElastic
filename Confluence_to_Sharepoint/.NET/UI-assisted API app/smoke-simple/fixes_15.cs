var normalizedPath = NormalizeServerRelativePath(pageServerRelativePath);
var (fieldsUrl, publishUrl, probeUrl) = BuildFileEndpoints(baseUri, normalizedPath);

// log the actual full URL we probe
Console.WriteLine($"[Probe URL] {probeUrl}");

using (var probe = await http.GetAsync(probeUrl))
{
    Console.WriteLine($"[Probe file exists] {normalizedPath} → HTTP {(int)probe.StatusCode} {probe.ReasonPhrase}");
    if (probe.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        // Fallback: resolve correct path from the list (by file name or title)
        var hint = normalizedPath.Split('/').Last(); // e.g., "First-Test-Page.aspx"
        normalizedPath = await ResolvePagePathFromListAsync(http, baseUri, hint);
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


// Try to find the page in "Site Pages" or "Pages" and return its FileRef (server-relative path)
static async Task<string> ResolvePagePathFromListAsync(HttpClient http, Uri baseUri, string hint)
{
    // Try both typical page libraries
    foreach (var listTitle in new[] { "Site Pages", "Pages" })
    {
        // If caller gave a title, turn it into a likely file name
        var fileLeaf = hint.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)
            ? hint
            : (hint.Replace(' ', '-') + ".aspx");

        string Escape(string s) => s.Replace("'", "''");

        // 1) Try exact file name (FileLeafRef)
        var byName = new Uri(baseUri,
            $"/_api/web/lists/getByTitle('{Escape(listTitle)}')/items" +
            $"?$select=Id,FileLeafRef,FileRef,Title&$filter=FileLeafRef eq '{Escape(fileLeaf)}'&$top=1");
        using (var r1 = await http.GetAsync(byName))
        {
            if (r1.IsSuccessStatusCode)
            {
                var json = await r1.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var arr = doc.RootElement.TryGetProperty("value", out var v) ? v
                        : (doc.RootElement.TryGetProperty("d", out var d) && d.TryGetProperty("results", out var res) ? res : default);
                if (arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                    return arr[0].GetProperty("FileRef").GetString()!;
            }
        }

        // 2) Try by Title (if hint wasn’t a .aspx), newest first
        if (!hint.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase))
        {
            var byTitle = new Uri(baseUri,
                $"/_api/web/lists/getByTitle('{Escape(listTitle)}')/items" +
                $"?$select=Id,FileLeafRef,FileRef,Title&$filter=Title eq '{Escape(hint)}'&$orderby=Modified desc&$top=1");
            using var r2 = await http.GetAsync(byTitle);
            if (r2.IsSuccessStatusCode)
            {
                var json = await r2.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var arr = doc.RootElement.TryGetProperty("value", out var v2) ? v2
                        : (doc.RootElement.TryGetProperty("d", out var d2) && d2.TryGetProperty("results", out var res2) ? res2 : default);
                if (arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                    return arr[0].GetProperty("FileRef").GetString()!;
            }
        }
    }

    throw new InvalidOperationException(
        $"Could not find a page matching '{hint}' in 'Site Pages' or 'Pages' on {baseUri}.");
}
