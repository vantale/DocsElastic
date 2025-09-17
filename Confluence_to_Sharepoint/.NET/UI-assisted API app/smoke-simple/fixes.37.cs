static async Task PrintWebContextAsync(HttpClient http, Uri baseUri)
{
    var url = new Uri(baseUri, "/_api/web?$select=Title,Url,ServerRelativeUrl");
    using var r = await http.GetAsync(url);
    var body = await r.Content.ReadAsStringAsync();
    Console.WriteLine($"[Web] {(int)r.StatusCode} {r.ReasonPhrase} → " +
                      (body.Length > 300 ? body[..300] + " …" : body));
}

static async Task ListSitePagesAsync(HttpClient http, Uri baseUri, string pageServerRelativePath)
{
    // derive folder from the path
    var idx = pageServerRelativePath.LastIndexOf('/');
    if (idx <= 0) { Console.WriteLine("[List] Bad path"); return; }
    var folder = pageServerRelativePath[..idx];

    var listUrl = new Uri(baseUri,
        $"/_api/web/GetFolderByServerRelativePath(decodedurl='{Uri.EscapeDataString(folder)}')/Files" +
        "?$select=Name,ServerRelativeUrl&$top=200");

    using var r = await http.GetAsync(listUrl);
    var body = await r.Content.ReadAsStringAsync();
    Console.WriteLine($"[List folder] {folder} → {(int)r.StatusCode} {r.ReasonPhrase}");
    if (!r.IsSuccessStatusCode)
    {
        Console.WriteLine(body.Length > 300 ? body[..300] + " …" : body);
        return;
    }

    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;
        System.Text.Json.JsonElement arr;
        if (root.TryGetProperty("value", out arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            for (int i = 0; i < arr.GetArrayLength(); i++)
            {
                var name = arr[i].GetProperty("Name").GetString();
                var sru  = arr[i].GetProperty("ServerRelativeUrl").GetString();
                Console.WriteLine($"  - {name} → {sru}");
            }
        }
        else if (root.TryGetProperty("d", out var d) &&
                 d.TryGetProperty("results", out arr) &&
                 arr.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            for (int i = 0; i < arr.GetArrayLength(); i++)
            {
                var name = arr[i].GetProperty("Name").GetString();
                var sru  = arr[i].GetProperty("ServerRelativeUrl").GetString();
                Console.WriteLine($"  - {name} → {sru}");
            }
        }
        else
        {
            Console.WriteLine("[List] Unexpected JSON (no array).");
            Console.WriteLine(body.Length > 300 ? body[..300] + " …" : body);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("[List] Parse error: " + ex.Message);
        Console.WriteLine(body.Length > 300 ? body[..300] + " …" : body);
    }
}

Console.WriteLine($"[Where] base='{baseUri}' path='{pageServerRelativePath}'");

// log attached cookie names (you likely already do)
var attached = handler.CookieContainer.GetCookies(baseUri);
Console.WriteLine("[Cookies attached] " + string.Join(", ", attached.Cast<NetCookie>().Select(k => k.Name)));

// ⬇️ add these two lines
await PrintWebContextAsync(http, baseUri);
await ListSitePagesAsync(http, baseUri, pageServerRelativePath);

// (optional) quick non-throwing probe
var probe = new Uri(baseUri,
    $"/_api/web/GetFileByServerRelativePath(decodedurl='{Uri.EscapeDataString(pageServerRelativePath)}')?$select=ServerRelativeUrl");
using (var r = await http.GetAsync(probe))
    Console.WriteLine($"[Probe ByPath] {(int)r.StatusCode} {r.ReasonPhrase}");

