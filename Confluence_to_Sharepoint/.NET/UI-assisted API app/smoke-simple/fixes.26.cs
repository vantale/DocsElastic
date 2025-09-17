// Requires: GetArrayOrThrow(JsonElement root, string context)
static async Task<string> ResolvePagePathAcrossLibrariesAsync(HttpClient http, System.Uri baseUri, string hint)
{
    // ----- local helpers -----
    static string Esc(string s) => s.Replace("'", "''"); // OData single-quote escape

    static string Normalize(string raw)
    {
        var p = System.Uri.UnescapeDataString(raw ?? string.Empty).Trim();
        if (!p.StartsWith("/")) p = "/" + p;
        while (p.EndsWith(";") || p.EndsWith(" ")) p = p[..^1];
        p = "/" + string.Join("/", p.Split('/', StringSplitOptions.RemoveEmptyEntries));
        return p;
    }

    // Accept either a plain array or an object with { results: [...] }
    static bool TryGetArray(JsonElement el, out JsonElement arr)
    {
        if (el.ValueKind == JsonValueKind.Array) { arr = el; return true; }
        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty("results", out var res) &&
            res.ValueKind == JsonValueKind.Array)
        { arr = res; return true; }
        arr = default;
        return false;
    }

    // Parse Search JSON that uses the OData shape (top-level "odata.metadata" is common).
    static string? ExtractServerRelativeFromSearchJson_OData(string json, string baseHost)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("PrimaryQueryResult", out var pqr)) return null;
        if (!pqr.TryGetProperty("RelevantResults", out var rr)) return null;
        if (!rr.TryGetProperty("Table", out var table)) return null;
        if (!table.TryGetProperty("Rows", out var rowsEl)) return null;

        if (!TryGetArray(rowsEl, out var rows)) return null;

        for (int i = 0; i < rows.GetArrayLength(); i++)
        {
            var row = rows[i];
            if (!row.TryGetProperty("Cells", out var cellsEl)) continue;
            if (!TryGetArray(cellsEl, out var cells)) continue;

            string? serverRel = null;
            string? pathAbs = null;

            for (int c = 0; c < cells.GetArrayLength(); c++)
            {
                var cell = cells[c];
                if (!cell.TryGetProperty("Key", out var kEl) || !cell.TryGetProperty("Value", out var vEl)) continue;
                var key = kEl.GetString();
                var val = vEl.GetString();

                if (string.Equals(key, "ServerRelativeUrl", StringComparison.OrdinalIgnoreCase)) serverRel = val;
                if (string.Equals(key, "Path", StringComparison.OrdinalIgnoreCase))             pathAbs  = val;
            }

            if (!string.IsNullOrWhiteSpace(serverRel))
            {
                if (!serverRel!.StartsWith("/")) serverRel = "/" + serverRel;
                if (serverRel.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)) return serverRel;
            }

            if (!string.IsNullOrWhiteSpace(pathAbs) &&
                System.Uri.TryCreate(pathAbs, System.UriKind.Absolute, out var abs) &&
                abs.Host.Equals(baseHost, StringComparison.OrdinalIgnoreCase))
            {
                var rel = abs.AbsolutePath;
                if (!rel.StartsWith("/")) rel = "/" + rel;
                if (rel.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)) return rel;
            }
        }

        return null;
    }
    // ----- end helpers -----

    // Build candidate file names from the hint
    var isFile   = hint.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase);
    var fileLeaf = isFile ? hint : (hint.Replace(' ', '-') + ".aspx");
    var stem     = isFile ? System.IO.Path.GetFileNameWithoutExtension(hint) : hint.Replace(' ', '-');

    // 1) Enumerate page libraries by template (119 = Site Pages, 850 = Publishing Pages)
    var listsUrl = new System.Uri(
        baseUri,
        "/_api/web/lists" +
        "?$select=Id,Title,BaseTemplate,RootFolder/ServerRelativeUrl" +
        "&$expand=RootFolder" +
        "&$filter=(BaseTemplate eq 119) or (BaseTemplate eq 850)");

    using (var lr = await http.GetAsync(listsUrl))
    {
        Console.WriteLine($"[Lists by template 119/850] HTTP {(int)lr.StatusCode} {lr.ReasonPhrase}");
        lr.EnsureSuccessStatusCode();

        var listsJson = await lr.Content.ReadAsStringAsync();
        using var listsDoc = JsonDocument.Parse(listsJson);
        var listsArr = GetArrayOrThrow(listsDoc.RootElement, "lists");

        for (int i = 0; i < listsArr.GetArrayLength(); i++)
        {
            var el           = listsArr[i];
            var listTitle    = el.TryGetProperty("Title", out var t) ? t.GetString() : "(no title)";
            var baseTemplate = el.GetProperty("BaseTemplate").GetInt32();
            var rootFolder   = el.GetProperty("RootFolder").GetProperty("ServerRelativeUrl").GetString();
            Console.WriteLine($"[List] {listTitle} (template {baseTemplate}) root='{rootFolder}'");

            // 2a) Exact file in the folder's Files collection
            var filesExact = new System.Uri(
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
                        return Normalize(sru);
                    }
                }
            }

            // 2b) Heuristic: name contains stem (handles auto-suffixed names like "-1.aspx")
            var filesLike = new System.Uri(
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
                        var picked = filesArr[0];
                        Console.WriteLine($"  [Files contains → pick] {picked.GetProperty("Name").GetString()}");
                        var sru = picked.GetProperty("ServerRelativeUrl").GetString()!;
                        return Normalize(sru);
                    }
                }
            }
        }
    }

    // 3) Last resort: SharePoint Search in this site (explicitly select needed fields)
    var q = isFile ? $"filename:{fileLeaf}" : stem;
    var selectProps = "Title,Path,ServerRelativeUrl";
    var searchUrl = new System.Uri(
        baseUri,
        $"/_api/search/query?querytext='{Esc(q)}'&rowlimit=20&trimduplicates=false&selectproperties='{System.Uri.EscapeDataString(selectProps)}'");

    using (var sr = await http.GetAsync(searchUrl))
    {
        Console.WriteLine($"[Search] {q} → {(int)sr.StatusCode} {sr.ReasonPhrase}");
        sr.EnsureSuccessStatusCode();

        var body = await sr.Content.ReadAsStringAsync();
        var sru = ExtractServerRelativeFromSearchJson_OData(body, baseUri.Host);

        if (!string.IsNullOrWhiteSpace(sru))
        {
            var normalized = Normalize(sru!);
            Console.WriteLine($"  [Search hit] {normalized}");
            return normalized;
        }

        // Optional: preview to debug unexpected shapes
        Console.WriteLine(body.Length > 600 ? body[..600] + " …" : body);
    }

    throw new InvalidOperationException($"Could not find a page matching '{hint}' in this site.");
}


// Verify CanvasContent1 + a few fields
static async Task VerifyPageAsync(HttpClient http, Uri baseUri, string serverRelativePath)
{
    var url = new Uri(baseUri,
        $"/_api/web/GetFileByServerRelativeUrl('{serverRelativePath.Replace("'", "''")}')/ListItemAllFields" +
        "?$select=Title,FileRef,UIVersionString,PromotedState,FirstPublishedDate,OData__ModerationStatus,CanvasContent1");

    using var resp = await http.GetAsync(url);
    Console.WriteLine($"[Verify GET] HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
    var json = await resp.Content.ReadAsStringAsync();
    Console.WriteLine(json.Length > 800 ? json.Substring(0,800) + " …" : json);

    // Optional: quick check for our marker text
    if (json.Contains("Smoke Update OK", StringComparison.OrdinalIgnoreCase))
        Console.WriteLine("✅ Canvas contains the smoke-test HTML.");
    else
        Console.WriteLine("ℹ️ CanvasContent1 returned, but marker text wasn’t found in this preview.");
}

// Parse SPO Search JSON that starts with "odata.metadata" (OData shape).
// Returns server-relative URL if found; else null.
static string? ExtractServerRelativeFromSearchJson_OData(string json, string baseHost)
{
    using var doc = System.Text.Json.JsonDocument.Parse(json);
    var root = doc.RootElement;

    // Navigate: PrimaryQueryResult → RelevantResults → Table → Rows (array or {results:[]})
    if (!root.TryGetProperty("PrimaryQueryResult", out var pqr)) return null;
    if (!pqr.TryGetProperty("RelevantResults", out var rr)) return null;
    if (!rr.TryGetProperty("Table", out var table)) return null;
    if (!table.TryGetProperty("Rows", out var rowsEl)) return null;

    // Rows can be array or object with "results"
    System.Text.Json.JsonElement rows =
        rowsEl.ValueKind == System.Text.Json.JsonValueKind.Array
            ? rowsEl
            : (rowsEl.TryGetProperty("results", out var resRows) && resRows.ValueKind == System.Text.Json.JsonValueKind.Array ? resRows : default);

    if (rows.ValueKind != System.Text.Json.JsonValueKind.Array) return null;

    for (int i = 0; i < rows.GetArrayLength(); i++)
    {
        var row = rows[i];
        if (!row.TryGetProperty("Cells", out var cellsEl)) continue;

        // Cells can be array or object with "results"
        System.Text.Json.JsonElement cells =
            cellsEl.ValueKind == System.Text.Json.JsonValueKind.Array
                ? cellsEl
                : (cellsEl.TryGetProperty("results", out var resCells) && resCells.ValueKind == System.Text.Json.JsonValueKind.Array ? resCells : default);

        if (cells.ValueKind != System.Text.Json.JsonValueKind.Array) continue;

        string? serverRel = null;
        string? pathAbs = null;

        for (int c = 0; c < cells.GetArrayLength(); c++)
        {
            var cell = cells[c];
            if (!cell.TryGetProperty("Key", out var kEl) || !cell.TryGetProperty("Value", out var vEl)) continue;
            var key = kEl.GetString();
            var val = vEl.GetString();

            if (string.Equals(key, "ServerRelativeUrl", StringComparison.OrdinalIgnoreCase)) serverRel = val;
            if (string.Equals(key, "Path", StringComparison.OrdinalIgnoreCase))             pathAbs  = val;
        }

        // Prefer server-relative if present
        if (!string.IsNullOrWhiteSpace(serverRel))
        {
            if (!serverRel!.StartsWith("/")) serverRel = "/" + serverRel;
            if (serverRel.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)) return serverRel;
        }

        // Fallback: absolute Path → server-relative (same host only)
        if (!string.IsNullOrWhiteSpace(pathAbs) &&
            System.Uri.TryCreate(pathAbs, System.UriKind.Absolute, out var abs) &&
            abs.Host.Equals(baseHost, StringComparison.OrdinalIgnoreCase))
        {
            var rel = abs.AbsolutePath;
            if (!rel.StartsWith("/")) rel = "/" + rel;
            if (rel.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)) return rel;
        }
    }

    return null;
}
