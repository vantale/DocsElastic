// Resolve Site Pages (or Pages) server-relative folder by probing candidates.
// Avoids brittle JSON parsing; works across OData shapes.
static async Task<string> ResolveSitePagesFolderSmartAsync(HttpClient http, Uri baseUri)
{
    // 1) Derive the server-relative root from contextinfo
    var ctxUrl = new Uri(baseUri, "/_api/contextinfo");
    using var ctx = await http.PostAsync(ctxUrl, new StringContent(""));
    Console.WriteLine($"[contextinfo] HTTP {(int)ctx.StatusCode} {ctx.ReasonPhrase}");
    ctx.EnsureSuccessStatusCode();
    var body = await ctx.Content.ReadAsStringAsync();

    string? full = null;
    using (var doc = JsonDocument.Parse(body))
    {
        var root = doc.RootElement;
        // nometadata first
        if (root.TryGetProperty("WebFullUrl", out var wfu) && wfu.ValueKind == JsonValueKind.String) full = wfu.GetString();
        if (full is null && root.TryGetProperty("SiteFullUrl", out var sfu) && sfu.ValueKind == JsonValueKind.String) full = sfu.GetString();

        // verbose fallback
        if (full is null && root.TryGetProperty("d", out var d) &&
            d.TryGetProperty("GetContextWebInformation", out var info))
        {
            if (info.TryGetProperty("WebFullUrl", out var w2) && w2.ValueKind == JsonValueKind.String) full = w2.GetString();
            if (full is null && info.TryGetProperty("SiteFullUrl", out var s2) && s2.ValueKind == JsonValueKind.String) full = s2.GetString();
        }
    }
    if (string.IsNullOrWhiteSpace(full))
        throw new InvalidOperationException("Could not read WebFullUrl/SiteFullUrl from contextinfo.");

    var rootUri = new Uri(full!, UriKind.Absolute);
    var rootPath = rootUri.AbsolutePath; // "/", "/sites/XYZ", "/teams/ABC", etc.
    if (string.IsNullOrWhiteSpace(rootPath)) rootPath = "/";

    // 2) Probe likely candidates with the modern API (no URL-encoding pitfalls)
    var candidates = new[]
    {
        "/SitePages",
        (rootPath.TrimEnd('/') + "/SitePages"),
        (rootPath.TrimEnd('/') + "/Pages")
    }
    .Select(p => p.StartsWith("/") ? p : "/" + p)
    .Distinct()
    .ToArray();

    foreach (var cand in candidates)
    {
        var check = new Uri(baseUri, $"/_api/web/GetFolderByServerRelativePath(DecodedUrl='{cand}')");
        using var resp = await http.GetAsync(check);
        Console.WriteLine($"[Probe folder] {cand} → HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        if (resp.IsSuccessStatusCode) return cand;
    }

    throw new InvalidOperationException(
        $"Could not resolve Site Pages/Pages folder. Tried: {string.Join(", ", candidates)}");
}
