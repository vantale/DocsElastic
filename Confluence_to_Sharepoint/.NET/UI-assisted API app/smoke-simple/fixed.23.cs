// Requires: GetArrayOrThrow(JsonElement root, string context)
// Finds a modern page's server-relative path by checking page libraries (template 119/850) and, if needed, Search.
static async Task<string> ResolvePagePathAcrossLibrariesAsync(HttpClient http, Uri baseUri, string hint)
{
    // ----- local helpers -----
    static string Esc(string s) => s.Replace("'", "''"); // OData single-quote escape

    static string Normalize(string raw)
    {
        var p = Uri.UnescapeDataString(raw ?? string.Empty).Trim();
        if (!p.StartsWith("/")) p = "/" + p;
        while (p.EndsWith(";") || p.EndsWith(" ")) p = p[..^1];
        p = "/" + string.Join("/", p.Split('/', StringSplitOptions.RemoveEmptyEntries));
        return p;
    }

    static string? ExtractServerRelativeFromSearchJson(string json, string baseHost)
    {
        using var doc = JsonDocument.Parse(json);

        static bool TryGetRows(JsonElement root, out JsonElement rows)
        {
            rows = default;
            if (root.TryGetProperty("PrimaryQueryResult", out var pqr) &&
                pqr.TryGetProperty("RelevantResults", out var rr) &&
                rr.TryGetProperty("Table", out var tbl) &&
                tbl.TryGetProperty("Rows", out var rowsEl))
            {
                rows = rowsEl.TryGetProperty("results", out var res) ? res : rowsEl;
                return rows.ValueKind == JsonValueKind.Array;
            }
            if (root.TryGetProperty("d", out var d) &&
                d.TryGetProperty("query", out var q) &&
                q.TryGetProperty("PrimaryQueryResult", out var pqr2) &&
                pqr2.TryGetProperty("RelevantResults", out var rr2) &&
                rr2.TryGetProperty("Table", out var tbl2) &&
                tbl2.TryGetProperty("Rows", out var rowsEl2))
            {
                rows = rowsEl2.TryGetProperty("results", out var res2) ? res2 : rowsEl2;
                return rows.ValueKind == JsonValueKind.Array;
            }
            return false;
        }

        if (!TryGetRows(doc.RootElement, out var rows)) return null;

        for (int i = 0; i < rows.GetArrayLength(); i++)
        {
            var row = rows[i];
            if (!row.TryGetProperty("Cells", out var cellsEl)) continue;
            var cells = cellsEl.TryGetProperty("results", out var res) ? res : cellsEl;
            if (cells.ValueKind != JsonValueKind.Array) continue;

            string? serverRel = null;
            string? pathAbs = null;

            for (int c = 0; c < cells.GetArrayLength(); c++)
            {
                var cell = cells[c];
                if (!cell.TryGetProperty("Key", out var kEl) || !cell.TryGetProperty("Value", out var vEl)) continue;
                var key = kEl.GetString();
                var val = vEl.GetString();
                if (string.Equals(key, "ServerRelativeUrl", StringComparison.OrdinalIgnoreCase)) serverRel = val;
                else if (string.Equals(key, "Path", StringComparison.OrdinalIgnoreCase)) pathAbs = val;
            }

            // Prefer direct server-relative hit
            if (!string.IsNullOrWhiteSpace(serverRel))
            {
                if (!serverRel!.StartsWith("/")) serverRel = "/" + serverRel;
                if (serverRel.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase) &&
                   (serverRel.Contains("/SitePages/", StringComparison.OrdinalIgnoreCase) ||
                    serverRel.Contains("/Pages/", StringComparison.OrdinalIgnoreCase)))
                    return serverRel;
            }

            // Fallback: absolute Path → server-relative (host must match)
            if (!string.IsNullOrWhiteSpace(pathAbs) && Uri.TryCreate(pathAbs, UriKind.Absolute, out var abs))
            {
                if (abs.Host.Equals(baseHost, StringComparison.OrdinalIgnoreCase))
                {
                    var rel = abs.AbsolutePath;
                    if (!rel.StartsWith("/")) rel = "/" + rel;
                    if (rel.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase) &&
                       (rel.Contains("/SitePages/", StringComparison.OrdinalIgnoreCase) ||
                        rel.Contains("/Pages/", StringComparison.OrdinalIgnoreCase)))
                        return rel;
                }
            }
        }
        return null;
    }
    // ----- end helpers -----

    // Build candidate file names from the hint
    var isFile   = hint.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase);
    var fileLeaf = isFile ? hint : (hint.Replace(' ', '-') + ".aspx");
    var stem     = isFile ? hint.Substring(0, Math.Max(0, hint.Length - 5)) : hint.Replace(' ', '-');

    // 1) Enumerate page libraries by template (119=Site Pages, 850=Publishing Pages)
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
            var el = listsArr[i];
            var listTitle   = el.TryGetProperty("Title", out var t) ? t.GetString() : "(no title)";
            var baseTemplate= el.GetProperty("BaseTemplate").GetInt32();
            var rootFolder  = el.GetProperty("RootFolder").GetProperty("ServerRelativeUrl").GetString();
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

            // 2b) Heuristic: file name contains stem (handles auto-suffixed names like "-1.aspx")
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

    // 3) Last resort: SharePoint Search in this site
    var q = isFile ? $"filename:{Esc(fileLeaf)}" : Esc(stem);
    var searchUrl = new System.Uri(baseUri, $"/_api/search/query?querytext='{q}'&trimduplicates=false&rowlimit=20");

    using (var sr = await http.GetAsync(searchUrl))
    {
        Console.WriteLine($"[Search] {q} → {(int)sr.StatusCode} {sr.ReasonPhrase}");
        sr.EnsureSuccessStatusCode();

        var body = await sr.Content.ReadAsStringAsync();
        var sru = ExtractServerRelativeFromSearchJson(body, baseUri.Host);

        if (!string.IsNullOrWhiteSpace(sru))
        {
            var normalized = Normalize(sru!);
            Console.WriteLine($"  [Search hit] {normalized}");
            return normalized;
        }

        // (Optional) preview if nothing parsed
        Console.WriteLine(body.Length > 600 ? body[..600] + " …" : body);
    }

    throw new InvalidOperationException($"Could not find a page matching '{hint}' in this site.");
}
