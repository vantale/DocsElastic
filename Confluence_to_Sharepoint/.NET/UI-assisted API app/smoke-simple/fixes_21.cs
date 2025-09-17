Replace the whole ResolvePagePathAcrossLibrariesAsync with this version (Files-based)
// Requires: GetArrayOrThrow(JsonElement root, string context)
// Uses:     NormalizeServerRelativePath(string raw) if you added it earlier
static async Task<string> ResolvePagePathAcrossLibrariesAsync(HttpClient http, Uri baseUri, string hint)
{
    static string Esc(string s) => s.Replace("'", "''");

    // Derive candidate file names from the hint
    var isFile   = hint.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase);
    var fileLeaf = isFile ? hint : (hint.Replace('', '-') + ".aspx"); // hyphenate spaces, add .aspx
    var stem     = isFile ? Path.GetFileNameWithoutExtension(hint) : hint.Replace(' ', '-');

    // 1) Find all page libraries by template: 119 = Site Pages (modern), 850 = Publishing "Pages"
    var listsUrl = new Uri(
        baseUri,
        "/_api/web/lists" +
        "?$select=Id,Title,BaseTemplate,RootFolder/ServerRelativeUrl" +
        "&$expand=RootFolder" +
        "&$filter=(BaseTemplate eq 119) or (BaseTemplate eq 850)");

    using var lr = await http.GetAsync(listsUrl);
    Console.WriteLine($"[Lists by template 119/850] HTTP {(int)lr.StatusCode} {lr.ReasonPhrase}");
    lr.EnsureSuccessStatusCode();

    var listsJson = await lr.Content.ReadAsStringAsync();
    using var listsDoc = JsonDocument.Parse(listsJson);
    var listsArr = GetArrayOrThrow(listsDoc.RootElement, "lists");

    for (int i = 0; i < listsArr.GetArrayLength(); i++)
    {
        var el          = listsArr[i];
        var listTitle   = el.TryGetProperty("Title", out var t) ? t.GetString() : "(no title)";
        var baseTemplate= el.GetProperty("BaseTemplate").GetInt32();
        var rootFolder  = el.GetProperty("RootFolder").GetProperty("ServerRelativeUrl").GetString();

        Console.WriteLine($"[List] {listTitle} (template {baseTemplate}) root='{rootFolder}'");

        // 2a) Exact file name in the folder's Files collection
        var filesExact = new Uri(
            baseUri,
            $"/_api/web/GetFolderByServerRelativeUrl('{Esc(rootFolder)}')/Files" +
            $"?$select=Name,ServerRelativeUrl&$filter=Name eq '{Esc(fileLeaf)}'&$top=1");

        using (var fx = await http.GetAsync(filesExact))
        {
            Console.WriteLine($"  [Files exact] {fileLeaf} → {(int)fx.StatusCode}");
            if (fx.IsSuccessStatusCode)
            {
                var json = await fx.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var filesArr = GetArrayOrThrow(doc.RootElement, "files-exact");

                if (filesArr.GetArrayLength() > 0)
                {
                    var sru = filesArr[0].GetProperty("ServerRelativeUrl").GetString()!;
                    return NormalizeServerRelativePath != null ? NormalizeServerRelativePath(sru) : sru;
                }
            }
        }

        // 2b) Heuristic: look for files that *contain* the stem (helps when SharePoint appends -1, -2, etc.)
        var filesLike = new Uri(
            baseUri,
            $"/_api/web/GetFolderByServerRelativeUrl('{Esc(rootFolder)}')/Files" +
            $"?$select=Name,ServerRelativeUrl&$filter=substringof('{Esc(stem)}',Name)&$top=5");

        using (var fl = await http.GetAsync(filesLike))
        {
            Console.WriteLine($"  [Files contains] {stem} → {(int)fl.StatusCode}");
            if (fl.IsSuccessStatusCode)
            {
                var json = await fl.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var filesArr = GetArrayOrThrow(doc.RootElement, "files-like");

                if (filesArr.GetArrayLength() > 0)
                {
                    // pick the first (you can add extra sorting if you wish)
                    var sru = filesArr[0].GetProperty("ServerRelativeUrl").GetString()!;
                    Console.WriteLine($"  [Files contains → pick] {filesArr[0].GetProperty("Name").GetString()}");
                    return NormalizeServerRelativePath != null ? NormalizeServerRelativePath(sru) : sru;
                }
            }
        }
    }

    // 3) Last resort: Search API within this site (may lag if very fresh)
    var q = isFile ? $"filename:{Esc(fileLeaf)}" : Esc(stem);
    var searchUrl = new Uri(
        baseUri,
        $"/_api/search/query?querytext='{q}'&trimduplicates=false&rowlimit=5");

    using (var sr = await http.GetAsync(searchUrl))
    {
        Console.WriteLine($"[Search] {q} → {(int)sr.StatusCode}");
        if (sr.IsSuccessStatusCode)
        {
            var json = await sr.Content.ReadAsStringAsync();
            // Very light parser to find ServerRelativeUrl in search results
            if (json.IndexOf("ServerRelativeUrl", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // crude but effective: look for "/.../SitePages/....aspx"
                var idx = json.IndexOf("/SitePages/", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) idx = json.IndexOf("/Pages/", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var end = json.IndexOf(".aspx", idx, StringComparison.OrdinalIgnoreCase);
                    if (end > idx)
                    {
                        var sru = json.Substring(idx, end - idx + 5);
                        sru = NormalizeServerRelativePath != null ? NormalizeServerRelativePath(sru) : sru;
                        Console.WriteLine($"  [Search hit] {sru}");
                        return sru;
                    }
                }
            }
        }
    }

    throw new InvalidOperationException($"Could not find a page matching '{hint}' in this site.");
}

How to use it in your update flow (unchanged)

Inside UpdateExistingModernPageWithCookiesAsync(...), keep the probe + fallback you already added:

var normalizedPath = NormalizeServerRelativePath(pageServerRelativePath);
var (fieldsUrl, publishUrl, probeUrl) = BuildFileEndpoints(baseUri, normalizedPath);

Console.WriteLine($"[Probe URL] {probeUrl}");
using (var probe = await http.GetAsync(probeUrl))
{
    Console.WriteLine($"[Probe file exists] {normalizedPath} → HTTP {(int)probe.StatusCode} {probe.ReasonPhrase}");
    if (probe.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
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
