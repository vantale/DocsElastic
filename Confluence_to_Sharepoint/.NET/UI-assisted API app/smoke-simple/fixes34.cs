// Requires at top of file:
// using System.Net;
// using System.Net.Http;
// using System.Net.Http.Headers;
// using System.Text;
// using System.Text.Json;
// using System.Text.Encodings.Web;
//
// And define aliases once to avoid type clashes with Playwright cookies:
// using NetCookie = System.Net.Cookie;
// using PwCookie  = Microsoft.Playwright.BrowserContextCookiesResult; // or your Playwright cookie type

// Updates an EXISTING modern page using browser cookies (FedAuth/rtFa), then publishes.
// pagePathOrUrl: either a full URL (https://tenant/.../SitePages/Page.aspx) or a server-relative path (/sites/.../SitePages/Page.aspx)
static async Task UpdateExistingModernPageWithCoookiesAsync(
    AppConfig cfg,
    IReadOnlyList<PwCookie> cookies,
    string pagePathOrUrl,
    bool publish = true)
{
    // --- helpers (local) ---
    static (Uri baseUri, string serverRelativePath) ResolveBaseAndPath(AppConfig cfg, string input)
    {
        if (input.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var u = new Uri(input);
            var path = u.AbsolutePath; // /sites/.../SitePages/...
            // Determine site root (everything before /SitePages/ or /Pages/)
            var idx = path.IndexOf("/SitePages/", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) idx = path.IndexOf("/Pages/", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) throw new ArgumentException("URL must contain /SitePages/ or /Pages/.");
            var siteRoot = path[..idx];
            if (string.IsNullOrEmpty(siteRoot)) siteRoot = "/";
            var baseUrl = $"{u.Scheme}://{u.Host}{siteRoot}";
            return (new Uri(baseUrl), Normalize(path));
        }
        else
        {
            var baseUri = new Uri(cfg.TargetSiteUrl.TrimEnd('/'));
            return (baseUri, Normalize(input));
        }
    }

    static string Normalize(string raw)
    {
        var p = Uri.UnescapeDataString(raw ?? string.Empty).Trim();
        if (!p.StartsWith("/")) p = "/" + p;
        while (p.EndsWith(";") || p.EndsWith(" ")) p = p[..^1]; // strip stray semicolons/spaces
        // Collapse duplicate slashes
        p = "/" + string.Join("/", p.Split('/', StringSplitOptions.RemoveEmptyEntries));
        return p;
    }

    static string ExtractFormDigest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("FormDigestValue", out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString()!;
        if (root.TryGetProperty("d", out var d) &&
            d.TryGetProperty("GetContextWebInformation", out var info) &&
            info.TryGetProperty("FormDigestValue", out var v2) && v2.ValueKind == JsonValueKind.String)
            return v2.GetString()!;
        throw new InvalidOperationException("Could not read FormDigestValue from _api/contextinfo.");
    }

    static string MakeRandomHtml()
    {
        var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
        var id = Guid.NewGuid().ToString("N")[..8];
        return $@"<h2>Smoke Update OK</h2>
<p>Updated at <strong>{stamp}</strong>, id <code>{id}</code>.</p>
<ul>
  <li>Inserted by the smoke test (cookies flow).</li>
  <li>CanvasContent1 replaced with a Text web part.</li>
</ul>";
    }
    // --- end helpers ---

    var (baseUri, pageServerRelativePath) = ResolveBaseAndPath(cfg, pagePathOrUrl);

    // Build HttpClient with your captured cookies
    var handler = new HttpClientHandler
    {
        CookieContainer = new CookieContainer(),
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    };
    foreach (var c in cookies)
    {
        var name   = c.Name  ?? string.Empty;
        var value  = c.Value ?? string.Empty;
        var domain = (c.Domain ?? baseUri.Host).TrimStart('.');
        var path   = string.IsNullOrEmpty(c.Path) ? "/" : c.Path;
        if (!string.IsNullOrEmpty(name))
        {
            handler.CookieContainer.Add(baseUri, new NetCookie(name, value, path, domain));
        }
    }

    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json;odata=nometadata");

    // (Optional) Probe existence, but don't fail the run if 404
    var probe = new Uri(baseUri,
        $"/_api/web/GetFileByServerRelativePath(decodedurl='{Uri.EscapeDataString(pageServerRelativePath)}')?$select=ServerRelativeUrl");
    using (var pr = await http.GetAsync(probe))
    {
        Console.WriteLine($"[Probe file exists] {pageServerRelativePath} → {(int)pr.StatusCode} {pr.ReasonPhrase}");
        if (pr.StatusCode == HttpStatusCode.NotFound)
        {
            Console.WriteLine("  ℹ️ File not found probe returned 404. Continuing anyway (some tenants trim or require later calls).");
        }
        else
        {
            pr.EnsureSuccessStatusCode();
        }
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

    // 2) Build a minimal modern canvas with one Text web part containing random HTML
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
                                controlType = 3, // client-side web part
                                webPartId = TextWebPartId,
                                emphasis = 0,
                                position = new { zoneIndex = 1, sectionIndex = 1, controlIndex = 1, layoutIndex = 1, sectionFactor = 12 },
                                webPartData = new {
                                    id = TextWebPartId,
                                    instanceId = Guid.NewGuid().ToString(),
                                    title = "Text",
                                    dataVersion = "2.9",
                                    properties = new {
                                        Title = "",
                                        Text  = htmlContent // SPO will sanitize disallowed HTML
                                    },
                                    serverProcessedContent = new {
                                        htmlStrings = new { }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    };

    var canvasJson = JsonSerializer.Serialize(
        canvas,
        new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }
    );

    // 3) MERGE CanvasContent1 + ensure modern layout
    var fieldsUrl = new Uri(
        baseUri,
        $"/_api/web/GetFileByServerRelativePath(decodedurl='{Uri.EscapeDataString(pageServerRelativePath)}')/ListItemAllFields");

    var bodyObj = new
    {
        CanvasContent1 = canvasJson,
        PageLayoutType = "Article"
    };

    using (var req = new HttpRequestMessage(HttpMethod.Post, fieldsUrl))
    {
        req.Headers.TryAddWithoutValidation("X-RequestDigest", digest);
        req.Headers.TryAddWithoutValidation("IF-MATCH", "*");
        req.Headers.TryAddWithoutValidation("X-HTTP-Method", "MERGE");
        req.Content = new StringContent(JsonSerializer.Serialize(bodyObj), Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req);
        Console.WriteLine($"[Set CanvasContent1] HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        resp.EnsureSuccessStatusCode(); // typically 204
    }

    // 4) Publish (optional)
    if (publish)
    {
        var publishUrl = new Uri(
            baseUri,
            $"/_api/web/GetFileByServerRelativePath(decodedurl='{Uri.EscapeDataString(pageServerRelativePath)}')/Publish(StringParameter='Smoke update')");

        using var pubReq = new HttpRequestMessage(HttpMethod.Post, publishUrl);
        pubReq.Headers.TryAddWithoutValidation("X-RequestDigest", digest);

        using var pubResp = await http.SendAsync(pubReq);
        Console.WriteLine($"[Publish] HTTP {(int)pubResp.StatusCode} {pubResp.ReasonPhrase}");
        pubResp.EnsureSuccessStatusCode(); // usually 200
    }

    Console.WriteLine($"✅ Page updated & saved: {new Uri(baseUri, pageServerRelativePath)}");
}
