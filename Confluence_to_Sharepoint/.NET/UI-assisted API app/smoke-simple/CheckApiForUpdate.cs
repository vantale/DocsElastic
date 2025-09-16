// 1) Add required using directives (top of Program.cs)
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
// 2) Paste these helpers inside your Program class
// Minimal, robust extractor for FormDigestValue from _api/contextinfo (nometadata or verbose)
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

// Generates a unique, harmless HTML snippet for the smoke update
static string MakeRandomHtml()
{
    var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
    var id = Guid.NewGuid().ToString("N").Substring(0, 8);
    return $@"<h2>Smoke Update OK</h2>
<p>Updated at <strong>{stamp}</strong>, id <code>{id}</code>.</p>
<ul>
  <li>This content was inserted by the smoke test.</li>
  <li>It uses your Bearer token and SharePoint REST.</li>
</ul>";
}
// 3) The main method — use token → update canvas → publish
// Paste this inside your Program class:
// Updates an EXISTING modern page (server-relative path) with random HTML using your Bearer token,
// then publishes it. Example pagePath: "/sites/TeamX/SitePages/YourPage.aspx"
static async Task UpdateExistingModernPageWithTokenAsync(
    string siteUrl,
    string pageServerRelativePath,
    string accessToken,
    bool publish = true)
{
    if (!pageServerRelativePath.StartsWith("/"))
        throw new ArgumentException("pageServerRelativePath must be server-relative, starting with '/'.", nameof(pageServerRelativePath));

    var baseUri = new Uri(siteUrl.TrimEnd('/'));

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    http.DefaultRequestHeaders.Add("Accept", "application/json;odata=nometadata");

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
                                controlType = 3,      // client-side web part
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
                                        Text = htmlContent      // SPO will sanitize disallowed HTML
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

    // 3) MERGE CanvasContent1 (replace canvas) + ensure PageLayoutType=Article
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
        resp.EnsureSuccessStatusCode();
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
        pubResp.EnsureSuccessStatusCode();
    }

    Console.WriteLine($"✅ Page updated & saved: {new Uri(baseUri, pageServerRelativePath)}");
}
// What it does
// Uses your Bearer token (Authorization: Bearer …).
// Fetches FormDigestValue.
// Replaces the page canvas with a single Text web part that holds random HTML.
// Publishes the page (so you’ll see the change immediately).
// If you prefer to append instead of replace, first GET the current CanvasContent1, modify the JSON (push a new control), then MERGE it back. We can add that variant later.
// 4) Where to invoke it
// Right after your current API probe succeeds (and after you resolved the page’s server-relative path), call:
// Example invocation (you provide the token and page path)
var accessToken = grabbedTokenFromPlaywright; // <-- your Bearer token string
var siteUrl     = cfg.TargetSiteUrl;          // e.g., "https://tenant.sharepoint.com/sites/YourSite"
var pagePath    = "/sites/YourSite/SitePages/Smoke-Target.aspx";

await UpdateExistingModernPageWithTokenAsync(siteUrl, pagePath, accessToken, publish: true);

// Minimal invocation reminder

var token   = grabbedTokenFromPlaywright; // Bearer
var siteUrl = cfg.TargetSiteUrl;          // e.g. https://tenant.sharepoint.com/sites/YourSite
var page    = "/sites/YourSite/SitePages/Smoke-Target.aspx";

await UpdateExistingModernPageWithTokenAsync(siteUrl, page, token, publish: true);
