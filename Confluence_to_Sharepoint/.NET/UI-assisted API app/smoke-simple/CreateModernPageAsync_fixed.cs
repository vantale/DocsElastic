// --- Helper: robustly extract FormDigestValue from any OData shape ---
static string ExtractFormDigestValue(string json)
{
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;

    if (root.TryGetProperty("FormDigestValue", out var v1))
        return v1.GetString() ?? throw new InvalidOperationException("FormDigestValue is null.");

    if (root.TryGetProperty("GetContextWebInformation", out var info) &&
        info.TryGetProperty("FormDigestValue", out var v2))
        return v2.GetString() ?? throw new InvalidOperationException("FormDigestValue is null.");

    if (root.TryGetProperty("d", out var d) &&
        d.TryGetProperty("GetContextWebInformation", out var info2) &&
        info2.TryGetProperty("FormDigestValue", out var v3))
        return v3.GetString() ?? throw new InvalidOperationException("FormDigestValue is null.");

    throw new InvalidOperationException("Could not find FormDigestValue in _api/contextinfo response.");
}

// --- Main: create & publish a modern page; resilient to path issues & logs everything ---
static async Task CreateModernPageAsync(AppConfig cfg, IReadOnlyList<PwCookie> cookies, string pageNameSlug, string pageTitle)
{
    var baseUri = new Uri(cfg.TargetSiteUrl.TrimEnd('/'));

    // Build HttpClient with cookies (same pattern as your probe)
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

    // 0) Discover the web's ServerRelativeUrl to avoid path mistakes (/ or /sites/xyz)
    var webUrl = new Uri(baseUri, "/_api/web/ServerRelativeUrl");
    using (var wResp = await http.GetAsync(webUrl))
    {
        Console.WriteLine($"[ServerRelativeUrl] HTTP {(int)wResp.StatusCode} {wResp.ReasonPhrase}");
        wResp.EnsureSuccessStatusCode();
        var body = await wResp.Content.ReadAsStringAsync();
        using var wDoc = JsonDocument.Parse(body);
        var wRoot = wDoc.RootElement;
        var serverRelativeRoot =
            wRoot.TryGetProperty("ServerRelativeUrl", out var sru) ? sru.GetString() :
            (wRoot.TryGetProperty("d", out var d) && d.TryGetProperty("ServerRelativeUrl", out var sru2) ? sru2.GetString() : null)
            ?? throw new InvalidOperationException("Could not read ServerRelativeUrl.");
        // Normalize
        if (string.IsNullOrWhiteSpace(serverRelativeRoot)) throw new InvalidOperationException("ServerRelativeUrl was empty.");
        if (!serverRelativeRoot.StartsWith("/")) serverRelativeRoot = "/" + serverRelativeRoot;

        // Build SitePages paths from the discovered root
        var sitePagesFolder = $"{serverRelativeRoot.TrimEnd('/')}/SitePages";
        var pageRelPath    = $"{sitePagesFolder}/{pageNameSlug}.aspx";
        Console.WriteLine($"[Paths] root='{serverRelativeRoot}' folder='{sitePagesFolder}' page='{pageRelPath}'");

        // 0.1) Verify SitePages exists (helpful for diagnosing 404 vs. security trimming)
        var folderCheckUrl = new Uri(baseUri, $"/_api/web/GetFolderByServerRelativeUrl('{sitePagesFolder}')");
        using (var fResp = await http.GetAsync(folderCheckUrl))
        {
            Console.WriteLine($"[Check SitePages folder] HTTP {(int)fResp.StatusCode} {fResp.ReasonPhrase}");
            if (fResp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException($"Folder not found: {sitePagesFolder}. " +
                    "This site may not have a Site Pages library, or your URL scope/permissions are wrong.");
            }
            fResp.EnsureSuccessStatusCode();
        }

        // 1) Get a FormDigest (contextinfo)
        var ctxUrl = new Uri(baseUri, "/_api/contextinfo");
        string ctxBody;
        using (var ctxResp = await http.PostAsync(ctxUrl, new StringContent("")))
        {
            Console.WriteLine($"[contextinfo] HTTP {(int)ctxResp.StatusCode} {ctxResp.ReasonPhrase}");
            ctxResp.EnsureSuccessStatusCode();
            ctxBody = await ctxResp.Content.ReadAsStringAsync();
        }
        var digest = ExtractFormDigestValue(ctxBody);
        if (string.IsNullOrWhiteSpace(digest))
            throw new InvalidOperationException("FormDigestValue was empty.");

        // 2) Create the client-side page (templateFileType=3) using ServerRelativeUrl variant (no encoding pitfalls)
        var addUrl = new Uri(baseUri,
            $"/_api/web/GetFolderByServerRelativeUrl('{sitePagesFolder}')/Files/AddTemplateFile(" +
            $"urlOfFile='{pageRelPath}',templateFileType=3)");

        using (var addReq = new HttpRequestMessage(HttpMethod.Post, addUrl))
        {
            addReq.Headers.TryAddWithoutValidation("X-RequestDigest", digest);
            using var addResp = await http.SendAsync(addReq);
            Console.WriteLine($"[AddTemplateFile] {addUrl} → HTTP {(int)addResp.StatusCode} {addResp.ReasonPhrase}");
            addResp.EnsureSuccessStatusCode();
        }

        // 3) Set fields on the backing list item (Title, PageLayoutType)
        var fieldsUrl = new Uri(baseUri,
            $"/_api/web/GetFileByServerRelativePath(decodedurl='{Uri.EscapeDataString(pageRelPath)}')/ListItemAllFields");

        var fieldsBody = new StringContent(
            "{\"Title\":\"" + JsonEncodedText.Encode(pageTitle).ToString() + "\",\"PageLayoutType\":\"Article\"}",
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

        // 4) Publish the page
        var publishUrl = new Uri(baseUri,
            $"/_api/web/GetFileByServerRelativePath(decodedurl='{Uri.EscapeDataString(pageRelPath)}')" +
            "/Publish(StringParameter='Initial publish')");

        using (var pubReq = new HttpRequestMessage(HttpMethod.Post, publishUrl))
        {
            pubReq.Headers.TryAddWithoutValidation("X-RequestDigest", digest);
            using var pubResp = await http.SendAsync(pubReq);
            Console.WriteLine($"[Publish] HTTP {(int)pubResp.StatusCode} {pubResp.ReasonPhrase}");
            pubResp.EnsureSuccessStatusCode();
        }

        var fullPageUrl = new Uri(baseUri, pageRelPath);
        Console.WriteLine($"✅ Modern page created & published: {fullPageUrl}");
    }
}
