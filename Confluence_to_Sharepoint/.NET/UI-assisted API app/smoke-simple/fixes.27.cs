// Accepts OData search JSON (top-level "odata.metadata" is fine).
// Tries multiple properties and doesn't over-filter by host.
// Returns a server-relative path like "/sites/X/SitePages/Y.aspx" or null.
static string? ExtractServerRelativeFromSearchJson_AllShapes(string json, string? preferredHost = null)
{
    using var doc = System.Text.Json.JsonDocument.Parse(json);
    var root = doc.RootElement;

    static bool TryGetArray(System.Text.Json.JsonElement el, out System.Text.Json.JsonElement arr)
    {
        if (el.ValueKind == System.Text.Json.JsonValueKind.Array) { arr = el; return true; }
        if (el.ValueKind == System.Text.Json.JsonValueKind.Object &&
            el.TryGetProperty("results", out var res) &&
            res.ValueKind == System.Text.Json.JsonValueKind.Array)
        { arr = res; return true; }
        arr = default; return false;
    }

    // Navigate to rows (supports common OData & verbose-ish shapes)
    System.Text.Json.JsonElement rows;
    if (root.TryGetProperty("PrimaryQueryResult", out var pqr) &&
        pqr.TryGetProperty("RelevantResults", out var rr) &&
        rr.TryGetProperty("Table", out var tbl) &&
        tbl.TryGetProperty("Rows", out var rowsEl) &&
        TryGetArray(rowsEl, out rows))
    {
        // ok
    }
    else if (root.TryGetProperty("d", out var d) &&
             d.TryGetProperty("query", out var q) &&
             q.TryGetProperty("PrimaryQueryResult", out var pqr2) &&
             pqr2.TryGetProperty("RelevantResults", out var rr2) &&
             rr2.TryGetProperty("Table", out var tbl2) &&
             tbl2.TryGetProperty("Rows", out var rowsEl2) &&
             TryGetArray(rowsEl2, out rows))
    {
        // ok
    }
    else
    {
        return null;
    }

    // Helper: normalize to "/.../SitePages/Name.aspx"
    static string Normalize(string raw)
    {
        var p = System.Uri.UnescapeDataString(raw ?? string.Empty).Trim();
        if (!p.StartsWith("/")) p = "/" + p;
        while (p.EndsWith(";") || p.EndsWith(" ")) p = p[..^1];
        p = "/" + string.Join("/", p.Split('/', System.StringSplitOptions.RemoveEmptyEntries));
        return p;
    }

    for (int i = 0; i < rows.GetArrayLength(); i++)
    {
        if (!rows[i].TryGetProperty("Cells", out var cellsEl) || !TryGetArray(cellsEl, out var cells))
            continue;

        string? serverRel = null;
        string? pathAbs = null;
        string? originalPath = null;
        string? redirected = null;
        string? parentLink = null;
        string? spWebUrl = null;

        for (int c = 0; c < cells.GetArrayLength(); c++)
        {
            var cell = cells[c];
            if (!cell.TryGetProperty("Key", out var kEl) || !cell.TryGetProperty("Value", out var vEl)) continue;
            var key = kEl.GetString();
            var val = vEl.GetString();

            if      (string.Equals(key, "ServerRelativeUrl",    System.StringComparison.OrdinalIgnoreCase)) serverRel    = val;
            else if (string.Equals(key, "Path",                 System.StringComparison.OrdinalIgnoreCase)) pathAbs     = val;
            else if (string.Equals(key, "OriginalPath",         System.StringComparison.OrdinalIgnoreCase)) originalPath= val;
            else if (string.Equals(key, "ServerRedirectedURL",  System.StringComparison.OrdinalIgnoreCase)) redirected  = val;
            else if (string.Equals(key, "ParentLink",           System.StringComparison.OrdinalIgnoreCase)) parentLink  = val;
            else if (string.Equals(key, "SPWebUrl",             System.StringComparison.OrdinalIgnoreCase)) spWebUrl    = val;
        }

        // 1) Direct server-relative
        if (!string.IsNullOrWhiteSpace(serverRel) && serverRel!.EndsWith(".aspx", System.StringComparison.OrdinalIgnoreCase))
            return Normalize(serverRel!);

        // 2) Absolute paths → server-relative (Path, OriginalPath, ServerRedirectedURL)
        foreach (var abs in new[] { pathAbs, originalPath, redirected })
        {
            if (string.IsNullOrWhiteSpace(abs)) continue;
            if (System.Uri.TryCreate(abs, System.UriKind.Absolute, out var u))
            {
                // If preferredHost given, prefer that; otherwise accept any SharePoint host.
                if (preferredHost == null ||
                    u.Host.Equals(preferredHost, System.StringComparison.OrdinalIgnoreCase) ||
                    u.Host.EndsWith(".sharepoint.com",  System.StringComparison.OrdinalIgnoreCase) ||
                    u.Host.EndsWith(".sharepoint-df.com", System.StringComparison.OrdinalIgnoreCase))
                {
                    var rel = u.AbsolutePath;
                    if (rel.EndsWith(".aspx", System.StringComparison.OrdinalIgnoreCase))
                        return Normalize(rel);
                }
            }
        }

        // 3) Last resort: ParentLink + filename (covers some publishing results)
        if (!string.IsNullOrWhiteSpace(parentLink) && System.Uri.TryCreate(parentLink, System.UriKind.Absolute, out var p))
        {
            var rel = p.AbsolutePath;
            if (rel.EndsWith(".aspx", System.StringComparison.OrdinalIgnoreCase))
                return Normalize(rel);
        }
    }

    return null;
}

var q = isFile ? $"filename:{fileLeaf}" : stem;
// Ask for many props so whatever your tenant returns, we can use it.
var selectProps = "Title,Path,ServerRelativeUrl,OriginalPath,ServerRedirectedURL,ParentLink,SPWebUrl";

var searchUrl = new System.Uri(
    baseUri,
    $"/_api/search/query?querytext='{Esc(q)}'&rowlimit=20&trimduplicates=false&selectproperties='{System.Uri.EscapeDataString(selectProps)}'");

using (var sr = await http.GetAsync(searchUrl))
{
    Console.WriteLine($"[Search] {q} → {(int)sr.StatusCode} {sr.ReasonPhrase}");
    sr.EnsureSuccessStatusCode();

    var body = await sr.Content.ReadAsStringAsync();
    // IMPORTANT: pass null to not over-filter by host (some results may come from managed paths/alt hosts).
    var sru = ExtractServerRelativeFromSearchJson_AllShapes(body, preferredHost: null);

    if (!string.IsNullOrWhiteSpace(sru))
    {
        var normalized = sru!;
        Console.WriteLine($"  [Search hit] {normalized}");
        return normalized;
    }

    // Debug preview (short)
    Console.WriteLine(body.Length > 600 ? body[..600] + " …" : body);
}


// Temporary: dump keys in first row
static void DebugFirstRowKeys(string json)
{
    using var doc = System.Text.Json.JsonDocument.Parse(json);
    var root = doc.RootElement;
    if (!root.TryGetProperty("PrimaryQueryResult", out var pqr)) return;
    if (!pqr.TryGetProperty("RelevantResults", out var rr)) return;
    if (!rr.TryGetProperty("Table", out var tbl)) return;
    if (!tbl.TryGetProperty("Rows", out var rowsEl)) return;

    System.Text.Json.JsonElement rows;
    if (!(rowsEl.ValueKind == System.Text.Json.JsonValueKind.Array ||
         (rowsEl.ValueKind == System.Text.Json.JsonValueKind.Object && rowsEl.TryGetProperty("results", out rows))))
        return;

    if (rows.ValueKind == System.Text.Json.JsonValueKind.Object) rows = rows.GetProperty("results");
    if (rows.GetArrayLength() == 0) { Console.WriteLine("[Search debug] No rows."); return; }

    var row0 = rows[0];
    if (!row0.TryGetProperty("Cells", out var cellsEl)) return;
    var cells = cellsEl.ValueKind == System.Text.Json.JsonValueKind.Array ? cellsEl : cellsEl.GetProperty("results");

    var keys = new System.Collections.Generic.List<string>();
    for (int c = 0; c < cells.GetArrayLength(); c++)
    {
        var cell = cells[c];
        if (cell.TryGetProperty("Key", out var kEl)) keys.Add(kEl.GetString() ?? "?");
    }
    Console.WriteLine("[Search debug] Keys in row0: " + string.Join(", ", keys));
}

Call it right after var body = await sr.Content.ReadAsStringAsync(); if needed:

DebugFirstRowKeys(body);
