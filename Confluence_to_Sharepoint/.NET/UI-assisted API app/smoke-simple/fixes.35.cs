// ... earlier in the method ...
var (baseUri, pageServerRelativePath) = ResolveBaseAndPath(cfg, pagePathOrUrl);
Console.WriteLine($"[Where] base='{baseUri}' path='{pageServerRelativePath}'");

// Build HttpClient with cookies (existing code)
var handler = new HttpClientHandler
{
    CookieContainer = new CookieContainer(),
    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
};
foreach (var c in cookies)
{
    var name = c.Name ?? string.Empty;
    var value = c.Value ?? string.Empty;
    var domain = (c.Domain ?? baseUri.Host).TrimStart('.');
    var cookiePath = string.IsNullOrEmpty(c.Path) ? "/" : c.Path;
    if (!string.IsNullOrEmpty(name))
        handler.CookieContainer.Add(baseUri, new NetCookie(name, value, cookiePath, domain));
}
using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json;odata=nometadata");

// ⬇️ INSERT THE PROBE RIGHT HERE (before calling /_api/contextinfo)
var probe = new Uri(
    baseUri,
    $"/_api/web/GetFileByServerRelativePath(decodedurl='{Uri.EscapeDataString(pageServerRelativePath)}')?$select=ServerRelativeUrl");
using (var r = await http.GetAsync(probe))
{
    Console.WriteLine($"[Probe ByPath] {(int)r.StatusCode} {r.ReasonPhrase}");
    if (r.StatusCode == HttpStatusCode.NotFound)
    {
        // Try the Url variant too (sometimes helpful)
        var esc = pageServerRelativePath.Replace("'", "''");
        var probe2 = new Uri(baseUri, $"/_api/web/GetFileByServerRelativeUrl('{esc}')?$select=ServerRelativeUrl");
        using var r2 = await http.GetAsync(probe2);
        Console.WriteLine($"[Probe ByUrl ] {(int)r2.StatusCode} {r2.ReasonPhrase}");
    }
}

// ... then continue with:
// 1) POST /_api/contextinfo → get digest
// 2) MERGE CanvasContent1
// 3) Publish (optional)
