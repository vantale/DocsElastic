// using System.Net;
// using System.Net.Http;
// using System.Net.Http.Headers;
// using System.Text;
// using System.Text.Json;
// using System.Text.Encodings.Web;
// using NetCookie = System.Net.Cookie;
// using PwCookie  = Microsoft.Playwright.BrowserContextCookiesResult;

static async Task UpdateExistingModernPageWithCoookiesAsync(
    AppConfig cfg,
    IReadOnlyList<PwCookie> cookies,
    string pagePathOrUrl,
    bool publish = true)
{
    // ---------- helpers ----------
    static string Normalize(string raw)
    {
        var p = Uri.UnescapeDataString(raw ?? string.Empty).Trim();
        if (!p.StartsWith("/")) p = "/" + p;
        while (p.EndsWith(";") || p.EndsWith(" ")) p = p[..^1];
        p = "/" + string.Join("/", p.Split('/', StringSplitOptions.RemoveEmptyEntries));
        return p;
    }
    static string EscOData(string s) => (s ?? string.Empty).Replace("'", "''");

    static (Uri baseUri, string serverRelativePath) ResolveBaseAndPath(AppConfig cfg, string input)
    {
        // Full URL → split into site root + server-relative
        if (input.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var u = new Uri(input);
            var path = u.AbsolutePath; // /sites/<Team>/SitePages/...
            var idx = path.IndexOf("/SitePages/", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) idx = path.IndexOf("/Pages/", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) throw new ArgumentException("URL must contain /SitePages/ or /Pages/.");
            var siteRoot = path[..idx];
            if (string.IsNullOrEmpty(siteRoot)) siteRoot = "/";
            var baseUrl = $"{u.Scheme}://{u.Host}{siteRoot}";
            return (new Uri(baseUrl), Normalize(path));
        }
        // Server-relative → derive site root from path
        if (!input.StartsWith("/")) throw new ArgumentException("Server-relative path must start with '/'.");
        var norm = Normalize(input);
        var idx2 = norm.IndexOf("/SitePages/", StringComparison.OrdinalIgnoreCase);
        if (idx2 < 0) idx2 = norm.IndexOf("/Pages/", StringComparison.OrdinalIgnoreCase);
        if (idx2 < 0) throw new ArgumentException("Path must contain /SitePages/ or /Pages/.");
        var cfgUri = new Uri(cfg.TargetSiteUrl.TrimEnd('/'));
        var siteRoot2 = norm[..idx2]; // e.g. /sites/<Team>
        if (string.IsNullOrEmpty(siteRoot2)) siteRoot2 = "/";
        var baseUrl2 = $"{cfgUri.Scheme}://{cfgUri.Host}{siteRoot2}";
        return (new Uri(baseUrl2), norm);
    }

    static string ExtractFormDigest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("FormDigestValue", out var v) && v.ValueKind == JsonValueKind.String) return v.GetString()!;
        if (root.TryGetProperty("d", out var d) &&
            d.TryGetProperty("GetContextWebInformation", out var info) &&
            info.TryGetProperty("FormDigestValue", out var v2) && v2.ValueKind == JsonValueKind.String) return v2.GetString()!;
        throw new InvalidOperationException("Could not read FormDigestValue from _api/contextinfo.");
    }

    static async Task<bool> TryProbeFileAsync(HttpClient http, Uri baseUri, string serverRelativePath)
    {
        var byPath = new Uri(baseUri,
            $"/_api/web/GetFileByServerRelativePath(decodedurl='{Uri.EscapeDataString(serverRelativePath)}')?$select=UniqueId");
        using (var rA = await http.GetAsync(byPath))
        {
            Console.WriteLine($"[Probe A:ByPath] {serverRelativePath} → {(int)rA.StatusCode} {rA.ReasonPhrase}");
            if (rA.IsSuccessStatusCode) return true;
            if (rA.StatusCode != HttpStatusCode.NotFound) rA.EnsureSuccessStatusCode();
        }
        var byUrl = new Uri(baseUri,
            $"/_api/web/GetFileByServerRelativeUrl('{EscOData(serverRelativePath)}')?$select=UniqueId");
        using (var rB = await http.GetAsync(byUrl))
        {
            Console.WriteLine($"[Probe B:ByUrl ] {serverRelativePath} → {(int)rB.StatusCode} {rB.ReasonPhrase}");
            if (rB.IsSuccessStatusCode) return true;
            if (rB.StatusCode != HttpStatusCode.NotFound) rB.EnsureSuccessStatusCode();
        }
        return false;
    }

    static async Task<string?> ProbeAndResolveActualPathAsync(HttpClient http, Uri baseUri, string candidatePath)
    {
        if (await TryProbeFileAsync(http, baseUri, candidatePath))
            return candidatePath;

        var idx = candidatePath.LastIndexOf('/');
        if (idx <= 0) return null;
        var folder = candidatePath[..idx];
        var leaf   = candidatePath[(idx + 1)..];

        // List files in the folder
        var listUrl = new Uri(baseUri,
            $"/_api/web/GetFolderByServerRelativePath(decodedurl='{Uri.EscapeDataString(folder)}')/Files" +
            "?$select=Name,ServerRelativeUrl&$top=500");
        using var resp = await http.GetAsync(listUrl);
        Console.WriteLine($"[Folder list] {folder} → {(int)resp.StatusCode} {resp.ReasonPhrase}");
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        JsonElement arr;
        if (doc.RootElement.TryGetProperty("value", out arr) && arr.ValueKind == JsonValueKind.Array)
        {
            // exact match
            for (int i = 0; i < arr.GetArrayLength(); i++)
            {
                var name = arr[i].GetProperty("Name").GetString() ?? "";
                if (string.Equals(name, leaf, StringComparison.OrdinalIgnoreCase))
                {
                    var sru = arr[i].GetProperty("ServerRelativeUrl").GetString();
                    Console.WriteLine($"  [Match exact] {name} → {sru}");
                    return sru;
                }
            }
            // contains (handles -1.aspx, etc.)
            var stem = System.IO.Path.GetFileNameWithoutExtension(leaf);
            for (int i = 0; i < arr.GetArrayLength(); i++)
            {
                var name = arr[i].GetProperty("Name").GetString() ?? "";
                if (name.Contains(stem, StringComparison.OrdinalIgnoreCase))
                {
                    var sru = arr[i].GetProperty("ServerRelativeUrl").GetString();
                    Console.WriteLine($"  [Match contains] {name} → {sru}");
                    return sru;
                }
            }
        }
        return null;
    }

    static string MakeRandomHtml()
    {
        var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
        var id = Guid.NewGuid().ToString("N")[..8];
        return $@"<h2>Smoke Update OK</h2>
<p>Updated at <strong>{stamp}</strong>, id <code>{id}</code>.</p>
<ul><li>Inserted by the smoke test (cookies flow).</li><li>CanvasContent1 replaced with a Text web part.</li></ul>";
    }
    // ---------- end helpers ----------

    var (baseUri, pageServerRelativePath) = ResolveBaseAndPath(cfg, pagePathOrUrl);
    Console.WriteLine($"[Where] base='{baseUri}' path='{pageServerRelativePath}'");

    // Build HttpClient with cookies
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

    // Log cookie names attached (diagnostics)
    var attached = handler.CookieContainer.GetCookies(baseUri);
    Console.WriteLine("[Cookies attached] " + string.Join(", ", attached.Cast<NetCookie>().Select(k => k.Name)));

    // Probe and auto-correct the path if needed
    var resolved = await ProbeAndResolveActualPathAsync(http, baseUri, pageServerRelativePath);
    if (resolved is null)
    {
        Console.Error.WriteLine("❌ Could not locate the file via API. Check site URL, file name (hyphenation), and permissions.");
        return; // graceful exit instead of throwing
    }
    if (!string.Equals(resolved, pageServerRelativePath, StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"[Auto-correct] Using resolved path: {resolved}");
        pageServerRelativePath = resolved;
    }

    // 1) Get request digest
    var ctxUrl = new Uri(baseUri, "/_api/contextinfo");
    string digest;
    using (var ctxResp = await http.PostAsync(ctxUrl, new StringContent("")))
    {
        Console.WriteLine($"[contextinfo] HTTP {(int)ctxResp.StatusCode} {ctxResp.ReasonPhrase}");
        ctxResp.EnsureSuccessStatusCode();
        var ctxBody = await ctxResp.Content.ReadAsStringAsync();
        digest = ExtractFormDigest(ctxBody);
    }

    // 2) Prepare one-column Text web part canvas
    const string TextWebPartId = "d1d91016-032f-456d-98a4-721247c305e8";
    var htmlContent = MakeRandomHtml();
    var canvas = new
    {
        sections = new object[]
        {
            new {
                layout = "OneColumn",
                emphasis = 0,
                columns = new object[]
                {
                    new {
                        factor = 12,
                        controls = new object[]
                        {
                            new {
                                id = Guid.NewGuid().ToString(),
                                controlType = 3,
                                webPartId = TextWebPartId,
                                emphasis = 0,
                                position = new { zoneIndex = 1, sectionIndex = 1, controlIndex = 1, layoutIndex = 1, sectionFactor = 12 },
                                webPartData = new {
                                    id = TextWebPartId,
                                    instanceId = Guid.NewGuid().ToString(),
                                    title = "Text",
                                    dataVersion = "2.9",
                                    properties = new { Title = "", Text = htmlContent },
                                    serverProcessedContent = new { htmlStrings = new { } }
                                }
                            }
                        }
                    }
                }
            }
        }
    };
    var canvasJson = JsonSerializer.Serialize(canvas, new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });

    // 3) MERGE CanvasContent1 (try ByPath, fallback to ByUrl on 404)
    var fieldsByPath = new Uri(
        baseUri,
        $"/_api/web/GetFileByServerRelativePath(decodedurl='{Uri.EscapeDataString(pageServerRelativePath)}')/ListItemAllFields");
    var fieldsByUrl = new Uri(
        baseUri,
        $"/_api/web/GetFileByServerRelativeUrl('{EscOData(pageServerRelativePath)}')/ListItemAllFields");

    var bodyObj = new { CanvasContent1 = canvasJson, PageLayoutType = "Article" };

    async Task<bool> TryMergeAsync(Uri endpoint)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        req.Headers.TryAddWithoutValidation("X-RequestDigest", digest);
        req.Headers.TryAddWithoutValidation("IF-MATCH", "*");
        req.Headers.TryAddWithoutValidation("X-HTTP-Method", "MERGE");
        req.Content = new StringContent(JsonSerializer.Serialize(bodyObj), Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req);
        Console.WriteLine($"[Set CanvasContent1] {endpoint.PathAndQuery} → {(int)resp.StatusCode} {resp.ReasonPhrase}");
        if (resp.StatusCode == HttpStatusCode.NotFound) return false;
        resp.EnsureSuccessStatusCode(); // expect 204
        return true;
    }

    var merged = await TryMergeAsync(fieldsByPath);
    if (!merged)
    {
        Console.WriteLine("  ↪︎ Fallback to ByUrl variant…");
        merged = await TryMergeAsync(fieldsByUrl);
        if (!merged)
        {
            Console.Error.WriteLine("❌ Still 404 on MERGE. Check that the file truly exists at the printed path and you can open it in the browser.");
            return;
        }
    }

    // 4) Publish (try ByPath then ByUrl)
    if (publish)
    {
        var pubByPath = new Uri(
            baseUri,
            $"/_api/web/GetFileByServerRelativePath(decodedurl='{Uri.EscapeDataString(pageServerRelativePath)}')/Publish(StringParameter='Smoke update')");
        var pubByUrl = new Uri(
            baseUri,
            $"/_api/web/GetFileByServerRelativeUrl('{EscOData(pageServerRelativePath)}')/Publish(StringParameter='Smoke update')");

        async Task<bool> TryPublishAsync(Uri endpoint)
        {
            using var pubReq = new HttpRequestMessage(HttpMethod.Post, endpoint);
            pubReq.Headers.TryAddWithoutValidation("X-RequestDigest", digest);
            using var pubResp = await http.SendAsync(pubReq);
            Console.WriteLine($"[Publish] {endpoint.PathAndQuery} → {(int)pubResp.StatusCode} {pubResp.ReasonPhrase}");
            if (pubResp.StatusCode == HttpStatusCode.NotFound) return false;
            pubResp.EnsureSuccessStatusCode();
            return true;
        }

        var pubOk = await TryPublishAsync(pubByPath);
        if (!pubOk)
        {
            Console.WriteLine("  ↪︎ Fallback to ByUrl variant…");
            pubOk = await TryPublishAsync(pubByUrl);
            if (!pubOk)
            {
                Console.Error.WriteLine("❌ Still 404 on Publish.");
                return;
            }
        }
    }

    Console.WriteLine($"✅ Page updated & saved: {new Uri(baseUri, pageServerRelativePath)}");
}
