ResolvePagePathAcrossLibrariesAsync

// 3) Last resort: SharePoint Search in this site (explicitly select the fields we need)
var q = isFile ? $"filename:{Esc(fileLeaf)}" : Esc(stem);
var selectProps = "Title,Path,ServerRelativeUrl";
var searchUrl = new System.Uri(
    baseUri,
    $"/_api/search/query?querytext='{q}'&rowlimit=20&trimduplicates=false&selectproperties='{System.Uri.EscapeDataString(selectProps)}'");

using (var sr = await http.GetAsync(searchUrl))
{
    Console.WriteLine($"[Search] {q} → {(int)sr.StatusCode} {sr.ReasonPhrase}");
    sr.EnsureSuccessStatusCode();

    var body = await sr.Content.ReadAsStringAsync();
    var sru = ExtractServerRelativeFromSearchJson_OData(body, baseUri.Host);  // new helper below

    if (!string.IsNullOrWhiteSpace(sru))
    {
        var normalized = Normalize(sru!);
        Console.WriteLine($"  [Search hit] {normalized}");
        return normalized;
    }

    // Optional: short preview for debugging
    Console.WriteLine(body.Length > 600 ? body[..600] + " …" : body);
}


Put this inside your Program class; it handles the "odata.metadata"/OData result and falls back to Path:

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
