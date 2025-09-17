using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

// If you don't already have these aliases, add them:
using NetCookie = System.Net.Cookie;
using PwCookie  = Microsoft.Playwright.BrowserContextCookiesResult;


// Build an HttpClient that sends your Playwright cookies to SharePoint
static HttpClient BuildHttpClientWithCookies(Uri baseUri, IReadOnlyList<PwCookie> cookies)
{
    var handler = new HttpClientHandler
    {
        CookieContainer = new CookieContainer(),
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    };

    foreach (var c in cookies)
    {
        var name   = c.Name ?? string.Empty;
        var value  = c.Value ?? string.Empty;
        var domain = (c.Domain ?? baseUri.Host).TrimStart('.');
        var path   = string.IsNullOrEmpty(c.Path) ? "/" : c.Path;

        // Only add cookies for the target host (SharePoint) to avoid noise
        if (!domain.EndsWith(baseUri.Host, StringComparison.OrdinalIgnoreCase) &&
            !baseUri.Host.EndsWith(domain, StringComparison.OrdinalIgnoreCase))
            continue;

        handler.CookieContainer.Add(baseUri, new NetCookie(name, value, path, domain));
    }

    var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    http.DefaultRequestHeaders.Add("Accept", "application/json;odata=nometadata");
    return http;
}

// Extract FormDigestValue from _api/contextinfo (nometadata or verbose)
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

// Generate a small random HTML snippet for the smoke update
static string MakeRandomHtml()
{
    var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
    var id = Guid.NewGuid().ToString("N")[..8];
    return $@"<h2>Smoke Update OK</h2>
<p>Updated at <strong>{stamp}</strong>, id <code>{id}</code>.</p>
<ul>
  <li>Inserted by the smoke test.</li>
  <li>Cookie-based SharePoint REST.</li>
</ul>";
}


// Updates an EXISTING modern page using your Playwright cookies (no Bearer token needed).
// Example page path: "/sites/TeamX/SitePages/YourPage.aspx"
static async Task UpdateExistingModernPageWithCookiesAsync(
    AppConfig cfg,
    IReadOnlyList<PwCookie> cookies,
    string pageServerRelativePath,
    bool publish = true)
{
    if (!pageServerRelativePath.StartsWith("/"))
        throw new ArgumentException("pageServerRelativePath must start with '/'.", nameof(pageServerRelativePath));

    var baseUri = new Uri(cfg.TargetSiteUrl.TrimEnd('/'));
    using var http = BuildHttpClientWithCookies(baseUri, cookies);

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

    // 2) Build minimal modern canvas with one Text web part containing random HTML
    const string TextWebPartId = "d1d91016-032f-456d-98a4-721247c305e8";
    var htmlContent = MakeRandomHtml();

    var canvas = new
    {
        sections = new object[]
        {
            new
            {
                layout = "OneColumn",
                emphasis = 0,
                columns = new object[]
                {
                    new
                    {
                        factor = 12,
                        controls = new object[]
                        {
                            new
                            {
                                id = Guid.NewGuid().ToString(),
                                controlType = 3, // client-side web part
                                webPartId = TextWebPartId,
                                emphasis = 0,
                                position = new { zoneIndex = 1, sectionIndex = 1, controlIndex = 1, layoutIndex = 1, sectionFactor = 12 },
                                webPartData = new
                                {
                                    id = TextWebPartId,
                                    instanceId = Guid.NewGuid().ToString(),
                                    title = "Text",
                                    dataVersion = "2.9",
                                    properties = new
                                    {
                                        Title = "",
                                        Text = htmlContent // sanitized by SPO
                                    },
                                    serverProcessedContent = new { htmlStrings = new { } }
                                }
                            }
                        }
                    }
                }
            }
        }
    };

    var canvasJson = JsonSerializer.Serialize(canvas);

    // 3) MERGE CanvasContent1 (replace canvas) + ensure modern layout
    var fieldsUrl = new Uri(baseUri,
        $"/_api/web/GetFileByServerRelativePath(decodedurl='{Uri.EscapeDataString(pageServerRelativePath)}')/ListItemAllFields");

    var body = new
    {
        CanvasContent1 = canvasJson,
        PageLayoutType = "Article"
    };

    using (var req = new HttpRequestMessage(HttpMethod.Post, fieldsUrl))
    {
        req.Headers.TryAddWithoutValidation("X-RequestDigest", digest);
        req.Headers.TryAddWithoutValidation("IF-MATCH", "*");
        req.Headers.TryAddWithoutValidation("X-HTTP-Method", "MERGE");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req);
        Console.WriteLine($"[Set CanvasContent1] HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        resp.EnsureSuccessStatusCode(); // expect 204
    }

    // 4) Publish (optional)
    if (publish)
    {
        var publishUrl = new Uri(baseUri,
            $"/_api/web/GetFileByServerRelativePath(decodedurl='{Uri.EscapeDataString(pageServerRelativePath)}')/Publish(StringParameter='Smoke update')");

        using var pubReq = new HttpRequestMessage(HttpMethod.Post, publishUrl);
        pubReq.Headers.TryAddWithoutValidation("X-RequestDigest", digest);

        using var pubResp = await http.SendAsync(pubReq);
        Console.WriteLine($"[Publish] HTTP {(int)pubResp.StatusCode} {pubResp.ReasonPhrase}");
        pubResp.EnsureSuccessStatusCode(); // expect 200
    }

    Console.WriteLine($"✅ Page updated & saved: {new Uri(baseUri, pageServerRelativePath)}");
}


await CallSharePointApiAsync(cfg, pwCookies);
Console.WriteLine("✅ API probe completed.");

// Update an existing page (created earlier in UI or elsewhere)
var existingPagePath = "/sites/<YourSite>/SitePages/<ExistingPage>.aspx";
await UpdateExistingModernPageWithCookiesAsync(cfg, pwCookies, existingPagePath, publish: true);
