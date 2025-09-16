# EdgeCookieSmokeTest – Create Modern Page (API Write Verification)

This add-on verifies you can **create a modern (client-side) page** in SharePoint using the **existing cookie-based auth** (FedAuth/rtFa) from your smoke test app.

## What this does
1. Requests a **FormDigest** via `/_api/contextinfo`.
2. Creates a page file in **Site Pages** using `Files/AddTemplateFile(..., templateFileType=3)` (modern page).
3. Sets page fields (`Title`, `PageLayoutType=Article`) via `ListItemAllFields` (MERGE).
4. **Publishes** the page so it’s visible.
5. Prints the final page URL. Throws on non-2xx so CI fails fast.

> **Requires:** Your app to be already logged into SharePoint (cookie-based) and your `AppConfig` with `TargetSiteUrl` & `ApiCheckPath` set.

---

## 1) Paste this method into `Program.cs` (inside the `Program` class)

```csharp
// Creates a modern (client-side) page in the Site Pages library, then publishes it.
// Requirements: valid FedAuth/rtFa cookies for cfg.TargetSiteUrl.
// Side effects: prints HTTP statuses; throws on non-success so your app fails CI on errors.
static async Task CreateModernPageAsync(AppConfig cfg, IReadOnlyList<PwCookie> cookies, string pageNameSlug, string pageTitle)
{
    var baseUri = new Uri(cfg.TargetSiteUrl.TrimEnd('/'));
    var serverRelativeFolder = $"{baseUri.AbsolutePath.TrimEnd('/')}/SitePages";
    var serverRelativePage = $"{serverRelativeFolder}/{pageNameSlug}.aspx";

    // Build HttpClient with cookies (same approach as your API probe)
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
        var path = string.IsNullOrEmpty(c.Path) ? "/" : c.Path;
        handler.CookieContainer.Add(baseUri, new System.Net.Cookie(name, value, path, domain));
    }

    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    http.DefaultRequestHeaders.Add("Accept", "application/json;odata=nometadata");

    // 1) Get a request digest
    var ctxUrl = new Uri(baseUri, "/_api/contextinfo");
    using (var ctxResp = await http.PostAsync(ctxUrl, new StringContent("")))
    {
        Console.WriteLine($"[contextinfo] HTTP {(int)ctxResp.StatusCode} {ctxResp.ReasonPhrase}");
        ctxResp.EnsureSuccessStatusCode();
        var ctxBody = await ctxResp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(ctxBody);
        var digest = doc.RootElement
            .GetProperty("d")
            .GetProperty("GetContextWebInformation")
            .GetProperty("FormDigestValue")
            .GetString();

        if (string.IsNullOrEmpty(digest))
            throw new InvalidOperationException("FormDigestValue not returned by _api/contextinfo.");

        // 2) Create the client-side page (templateFileType=3)
        var addUrl = new Uri(baseUri,
            $"/_api/web/GetFolderByServerRelativePath(decodedurl='{Uri.EscapeDataString(serverRelativeFolder)}')" +
            $"/Files/AddTemplateFile(urlOfFile='{Uri.EscapeDataString(serverRelativePage)}',templateFileType=3)");

        using (var addReq = new HttpRequestMessage(HttpMethod.Post, addUrl))
        {
            addReq.Headers.TryAddWithoutValidation("X-RequestDigest", digest);
            using var addResp = await http.SendAsync(addReq);
            Console.WriteLine($"[AddTemplateFile] HTTP {(int)addResp.StatusCode} {addResp.ReasonPhrase}");
            addResp.EnsureSuccessStatusCode();
        }

        // 3) Set modern fields on the list item (Title, PageLayoutType)
        var fieldsUrl = new Uri(baseUri,
            $"/_api/web/GetFileByServerRelativePath(decodedurl='{Uri.EscapeDataString(serverRelativePage)}')/ListItemAllFields");

        var fieldsBody = new StringContent(
            "{"Title":"" + JsonEncodedText.Encode(pageTitle).ToString() + "","PageLayoutType":"Article"}",
            Encoding.UTF8, "application/json");
        using (var fieldsReq = new HttpRequestMessage(HttpMethod.Post, fieldsUrl))
        {
            fieldsReq.Headers.TryAddWithoutValidation("X-RequestDigest", digest);
            fieldsReq.Headers.TryAddWithoutValidation("IF-MATCH", "*");
            fieldsReq.Headers.TryAddWithoutValidation("X-HTTP-Method", "MERGE");
            fieldsReq.Content = fieldsBody;

            using var fieldsResp = await http.SendAsync(fieldsReq);
            Console.WriteLine($"[SetFields MERGE] HTTP {(int)fieldsResp.StatusCode} {fieldsResp.ReasonPhrase}");
            fieldsResp.EnsureSuccessStatusCode();
        }

        // 4) Publish (optional but typical)
        var publishUrl = new Uri(baseUri,
            $"/_api/web/GetFileByServerRelativePath(decodedurl='{Uri.EscapeDataString(serverRelativePage)}')" +
            "/Publish(StringParameter='Initial publish')");
        using (var pubReq = new HttpRequestMessage(HttpMethod.Post, publishUrl))
        {
            pubReq.Headers.TryAddWithoutValidation("X-RequestDigest", digest);
            using var pubResp = await http.SendAsync(pubReq);
            Console.WriteLine($"[Publish] HTTP {(int)pubResp.StatusCode} {pubResp.ReasonPhrase}");
            pubResp.EnsureSuccessStatusCode();
        }

        // Done
        var fullPageUrl = new Uri(baseUri, serverRelativePage);
        Console.WriteLine($"✅ Modern page created & published: {fullPageUrl}");
    }
}
```

> **Namespaces used**: `System`, `System.Net`, `System.Net.Http`, `System.Net.Http.Headers`, `System.Text`, `System.Text.Json`, `System.Text.Encodings.Web`, `System.Threading.Tasks`

Make sure these `using` directives exist at the top of `Program.cs`:

```csharp
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
```

---

## 2) Invoke it (where to paste the call)

Find this existing block in `Program.cs`:

```csharp
// 5) Call SharePoint REST using CookieContainer
await CallSharePointApiAsync(cfg, pwCookies);
Console.WriteLine("✅ API probe completed.");
```

**Add this _immediately after_ it:**

```csharp
// 6) Create a modern test page (unique slug recommended)
var slug = $"Smoke-Modern-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
await CreateModernPageAsync(cfg, pwCookies, slug, "Smoke Test Modern Page");
```

That’s all. On run, you should see status lines for:
- `/_api/contextinfo`
- `Files/AddTemplateFile(...)`
- `ListItemAllFields (MERGE)`
- `Publish(...)`
- and finally: **✅ Modern page created & published: https://.../SitePages/Smoke-Modern-<timestamp>.aspx**

---

## 3) Rollback / Cleanup (optional)

To remove the test page later, delete it from **Site Pages** in the UI or via REST:

```http
POST /_api/web/GetFileByServerRelativePath(decodedurl='/sites/<site>/SitePages/Smoke-Modern-<ts>.aspx')/recycle
Headers:
  X-RequestDigest: <value>
  IF-MATCH: *
  X-HTTP-Method: DELETE
```

---

## 4) Troubleshooting

- **401/403** on AddTemplateFile → you have read-only rights to Site Pages. Ask for Contribute or higher.
- **302** to login → cookie scoped to a different host/site; ensure `TargetSiteUrl` points to the site collection you’re testing.
- **415/400** on MERGE → check headers (`X-HTTP-Method: MERGE`) and `Content-Type: application/json`.
- **Page opens in classic** → verify `templateFileType=3` and `PageLayoutType=Article`.
