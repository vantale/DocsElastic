// Robustly extract FormDigestValue from any OData shape (nometadata/minimal/verbose)
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

// Helper: try to read one of several property names from the same JSON root (nometadata or verbose)
static string? TryGetString(JsonElement root, params string[] names)
{
    foreach (var n in names)
    {
        if (root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
    }
    if (root.TryGetProperty("d", out var d))
    {
        foreach (var n in names)
        {
            if (d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
    }
    return null;
}

// Create & publish a modern page; derives serverRelativeRoot from WebFullUrl/SiteFullUrl in contextinfo
static async Task CreateModernPageAsync(AppConfig cfg, IReadOnlyList<PwCookie> cookies, string pageNameSlug, string pageTitle)
{
    var baseUri = new Uri(cfg.TargetSiteUrl.TrimEnd('/'));

    // HttpClient with cookies
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

    // 1) Get context info (digest + URLs we’ll use to derive ServerRelativeUrl)
    var ctxUrl = new Uri(baseUri, "/_api/contextinfo");
    string ctxBody;
    using (var ctxResp = await http.PostAsync(ctxUrl, new StringContent("")))
    {
        Console.WriteLine($"[contextinfo] HTTP {(int)ctxResp.StatusCode} {ctxResp.ReasonPhrase}");
        ctxResp.EnsureSuccessStatusCode();
        ctxBody = await ctxResp.Content.ReadAsStringAsync();
    }

    using var ctxDoc = JsonDocument.Parse(ctxBody);
    var ctxRoot = ctxDoc.RootElement;

    var digest = ExtractFormDigestValue(ctxBody);
    if (string.IsNullOrWhiteSpace(digest))
        throw new InvalidOperationException("FormDigestValue was empty.");

    // Prefer WebFullUrl, then SiteFullUrl; both appear in your response
    var webFullUrl = TryGetString(ctxRoot, "WebFullUrl");
    var siteFullUrl = TryGetString(ctxRoot, "SiteFullUrl");
    var full = webFullUrl ?? siteFullUrl ?? throw new InvalidOperationException("Could not read WebFullUrl or SiteFullUrl from contextinfo.");
    var fullUri = new Uri(full, UriKind.Absolute);
    var serverRelativeRoot = fullUri.AbsolutePath;            // e.g. "/", "/sites/MySite", "/teams/XYZ"
    if (string.IsNullOrWhiteSpace(serverRelativeRoot)) serverRelativeRoot = "/";

    var sitePagesFolder = $"{serverRelativeRoot.TrimEnd('/')}/SitePages";
    var pageRelPath = $"{sitePagesFolder}/{pageNameSlug}.aspx";
    Console.WriteLine($"[Paths] root='{serverRelativeRoot}' folder='{sitePagesFolder}' page='{pageRelPath}'");

    // 2) Verify SitePages exists (clear 404 vs. scope/perm issues early)
    var folderCheckUrl = new Uri(baseUri, $"/_api/web/GetFolderByServerRelativeUrl('{sitePagesFolder}')");
    using (var fResp = await http.GetAsync(folderCheckUrl))
    {
        Console.WriteLine($"[Check SitePages folder] HTTP {(int)fResp.StatusCode} {fResp.ReasonPhrase}");
        if (fResp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException($"Folder not found: {sitePagesFolder}. This web may not have a Site Pages library, or your scope/permissions are wrong.");
        fResp.EnsureSuccessStatusCode();
    }

    // 3) Create the client-side page (templateFileType=3) — use ServerRelativeUrl variant
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

    // 4) Set fields (Title, PageLayoutType)
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

    // 5) Publish
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
