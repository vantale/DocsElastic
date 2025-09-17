// Requires: GetArrayOrThrow(JsonElement root, string context)
// Uses (optional): NormalizeServerRelativePath(string raw) if you already have it
static async Task<string> ResolvePagePathAcrossLibrariesAsync(HttpClient http, Uri baseUri, string hint)
{
    static string Esc(string s) => s.Replace("'", "''");

    // 1) Enumerate candidate page libraries by template:
    //    119 = Site Pages (modern), 850 = Publishing Pages
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

    var isFile = hint.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase);
    var fileLeaf = isFile ? hint : (hint.Replace(' ', '-') + ".aspx");

    for (int i = 0; i < listsArr.GetArrayLength(); i++)
    {
        var el = listsArr[i];
        var listId = el.GetProperty("Id").GetString();
        var listTitle = el.TryGetProperty("Title", out var t) ? t.GetString() : "(no title)";
        var baseTemplate = el.GetProperty("BaseTemplate").GetInt32();
        var rootFolder = el.GetProperty("RootFolder").GetProperty("ServerRelativeUrl").GetString();

        Console.WriteLine($"[List] {listTitle} (template {baseTemplate}) root='{rootFolder}'");

        // 2a) Try by FileLeafRef (exact file name) — first attempt (FileRef present)
        var byLeaf = new Uri(
            baseUri,
            $"/_api/web/lists(guid'{listId}')/items" +
            $"?$select=Id,FileLeafRef,FileRef,Title" +
            $"&$filter=FileLeafRef eq '{Esc(fileLeaf)}'" +
            $"&$top=1");

        using (var r1 = await http.GetAsync(byLeaf))
        {
            Console.WriteLine($"  [Query by leaf] {fileLeaf} → {(int)r1.StatusCode}");
            if (r1.IsSuccessStatusCode)
            {
                var j = await r1.Content.ReadAsStringAsync();
                using var d1 = JsonDocument.Parse(j);
                var items = GetArrayOrThrow(d1.RootElement, "items-by-leaf");
                Console.WriteLine($"  [By leaf results] {items.GetArrayLength()}");

                if (items.GetArrayLength() > 0 &&
                    items[0].TryGetProperty("FileRef", out var frEl) &&
                    frEl.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(frEl.GetString()))
                {
                    var path = frEl.GetString()!;
                    return NormalizeServerRelativePath != null
                        ? NormalizeServerRelativePath(path)
                        : path;
                }
            }
        }

        // 2a-2) Same query but expand File to read File/ServerRelativeUrl (some shapes don’t include FileRef)
        var byLeafExpand = new Uri(
            baseUri,
            $"/_api/web/lists(guid'{listId}')/items" +
            $"?$select=Id,Title,File/ServerRelativeUrl" +
            $"&$expand=File" +
            $"&$filter=FileLeafRef eq '{Esc(fileLeaf)}'" +
            $"&$top=1");

        using (var r1b = await http.GetAsync(byLeafExpand))
        {
            Console.WriteLine($"  [Query by leaf + expand File] {fileLeaf} → {(int)r1b.StatusCode}");
            if (r1b.IsSuccessStatusCode)
            {
                var j = await r1b.Content.ReadAsStringAsync();
                using var d1b = JsonDocument.Parse(j);
                var items = GetArrayOrThrow(d1b.RootElement, "items-by-leaf-expand");
                Console.WriteLine($"  [By leaf + expand results] {items.GetArrayLength()}");

                if (items.GetArrayLength() > 0 &&
                    items[0].TryGetProperty("File", out var fileObj) &&
                    fileObj.TryGetProperty("ServerRelativeUrl", out var sruEl) &&
                    sruEl.ValueKind == JsonValueKind.String)
                {
                    var path = sruEl.GetString()!;
                    return NormalizeServerRelativePath != null
                        ? NormalizeServerRelativePath(path)
                        : path;
                }
            }
        }

        // 2b) If hint looked like a title, try by Title (newest first)
        if (!isFile)
        {
            var byTitle = new Uri(
                baseUri,
                $"/_api/web/lists(guid'{listId}')/items" +
                $"?$select=Id,FileLeafRef,FileRef,Title" +
                $"&$filter=Title eq '{Esc(hint)}'" +
                $"&$orderby=Modified desc" +
                $"&$top=1");

            using (var r2 = await http.GetAsync(byTitle))
            {
                Console.WriteLine($"  [Query by title] {hint} → {(int)r2.StatusCode}");
                if (r2.IsSuccessStatusCode)
                {
                    var j = await r2.Content.ReadAsStringAsync();
                    using var d2 = JsonDocument.Parse(j);
                    var items = GetArrayOrThrow(d2.RootElement, "items-by-title");
                    Console.WriteLine($"  [By title results] {items.GetArrayLength()}");

                    if (items.GetArrayLength() > 0 &&
                        items[0].TryGetProperty("FileRef", out var fr2El) &&
                        fr2El.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(fr2El.GetString()))
                    {
                        var path = fr2El.GetString()!;
                        return NormalizeServerRelativePath != null
                            ? NormalizeServerRelativePath(path)
                            : path;
                    }
                }
            }

            // 2b-2) Title path with expand File as a fallback
            var byTitleExpand = new Uri(
                baseUri,
                $"/_api/web/lists(guid'{listId}')/items" +
                $"?$select=Id,Title,File/ServerRelativeUrl" +
                $"&$expand=File" +
                $"&$filter=Title eq '{Esc(hint)}'" +
                $"&$orderby=Modified desc" +
                $"&$top=1");

            using var r2b = await http.GetAsync(byTitleExpand);
            Console.WriteLine($"  [Query by title + expand File] {hint} → {(int)r2b.StatusCode}");
            if (r2b.IsSuccessStatusCode)
            {
                var j = await r2b.Content.ReadAsStringAsync();
                using var d2b = JsonDocument.Parse(j);
                var items = GetArrayOrThrow(d2b.RootElement, "items-by-title-expand");
                Console.WriteLine($"  [By title + expand results] {items.GetArrayLength()}");

                if (items.GetArrayLength() > 0 &&
                    items[0].TryGetProperty("File", out var fileObj2) &&
                    fileObj2.TryGetProperty("ServerRelativeUrl", out var sru2El) &&
                    sru2El.ValueKind == JsonValueKind.String)
                {
                    var path = sru2El.GetString()!;
                    return NormalizeServerRelativePath != null
                        ? NormalizeServerRelativePath(path)
                        : path;
                }
            }
        }
    }

    throw new InvalidOperationException(
        $"Could not find a page matching '{hint}' in any Site Pages/Pages library on {baseUri}.");
}
