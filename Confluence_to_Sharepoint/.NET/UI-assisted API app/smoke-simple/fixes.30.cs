// 3) Last resort: SharePoint Search in this site (explicitly select needed fields)
var q = isFile ? $"filename:{fileLeaf}" : stem;
var selectProps = "Title,Path,ServerRelativeUrl,OriginalPath,ServerRedirectedURL,ParentLink,SPWebUrl";
var searchUrl = new System.Uri(
    baseUri,
    $"/_api/search/query?querytext='{Esc(q)}'&rowlimit=20&trimduplicates=false&selectproperties='{System.Uri.EscapeDataString(selectProps)}'");

using (var sr = await http.GetAsync(searchUrl))
{
    Console.WriteLine($"[Search] {q} → {(int)sr.StatusCode} {sr.ReasonPhrase}");
    sr.EnsureSuccessStatusCode();

    var body = await sr.Content.ReadAsStringAsync();

    // ⬇️ INSERT THE DEBUG CALL *RIGHT HERE*
    DebugFirstRowKeys(body);  // temporary: prints which keys exist in row0

    // then attempt extraction
    var sru = ExtractServerRelativeFromSearchJson_AllShapes(body, preferredHost: null);

    if (!string.IsNullOrWhiteSpace(sru))
    {
        var normalized = Normalize(sru!);
        Console.WriteLine($"  [Search hit] {normalized}");
        return normalized;
    }

    // Optional: short preview for troubleshooting
    Console.WriteLine(body.Length > 600 ? body[..600] + " …" : body);
}
