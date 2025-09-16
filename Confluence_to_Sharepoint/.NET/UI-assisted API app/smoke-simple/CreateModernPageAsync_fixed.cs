// --- Helper: robustly extract FormDigestValue from any OData shape ---
static string ExtractFormDigestValue(string json)
{
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;

    // OData=nometadata (your case): digest at root
    if (root.TryGetProperty("FormDigestValue", out var v1))
        return v1.GetString() ?? throw new InvalidOperationException("FormDigestValue is null.");

    // OData=minimalmetadata: nested under GetContextWebInformation
    if (root.TryGetProperty("GetContextWebInformation", out var info) &&
        info.TryGetProperty("FormDigestValue", out var v2))
        return v2.GetString() ?? throw new InvalidOperationException("FormDigestValue is null.");

    // OData=verbose: d -> GetContextWebInformation -> FormDigestValue
    if (root.TryGetProperty("d", out var d) &&
        d.TryGetProperty("GetContextWebInformation", out var info2) &&
        info2.TryGetProperty("FormDigestValue", out var v3))
        return v3.GetString() ?? throw new InvalidOperationException("FormDigestValue is null.");

    throw new InvalidOperationException("Could not find FormDigestValue in _api/contextinfo response.");
}

// --- Main: create & publish a modern page using cookie-based auth ---
static async Task CreateModernPageAsync(AppConfig cfg, IReadOnlyList<PwCookie> cookies, string pageNameSlug, string pageTitle)
{
    var baseUri = new Uri(cfg.TargetSiteUrl.TrimEnd('/'));
    var serverRelativeFolder = $"{baseUri.AbsolutePath.TrimEnd('/')}/SitePages";
    var serverRelativePage = $"{serverRelativeFolder}/{pageNameSlug}.aspx";

    // HttpClient with captured cookies (same pattern as your probe)
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

    // 2) Create a modern (client-side) page file
    var addUrl = new Uri(
        baseUri,
        $"/_api/web/GetFolderByServerRelativePath(decodedurl='{Uri.EscapeDataString(serverRelativeFolder)}')" +
        $"/Files/AddTemplateFile(urlOfFile='{Uri.EscapeDataString(serverRelativePage)}',templateFileType=3)");

    using (var addReq = new HttpRequestMessage(HttpMethod.Post, addUrl))
    {
        addReq.Headers.TryAddWithoutValidation("X-RequestDigest", digest);
        using var addResp = await http.SendAsync(addReq);
        Console.WriteLine($"[AddTemplateFile] HTTP {(int)addResp.StatusCode} {addResp.ReasonPhrase}");
        addResp.EnsureSuccessStatusCode();
    }

    // 3) Set fields on the backing list item (Title, PageLayoutType)
    var fieldsUrl = new Uri(
        baseUri,
        $"/_api/web/GetFileByServerRelativePath(decodedurl='{Uri.EscapeDataString(serverRelativePage)}')/ListItemAllFields");

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
    var publishUrl = new Uri(
        baseUri,
        $"/_api/web/GetFileByServerRelativePath(decodedurl='{Uri.EscapeDataString(serverRelativePage)}')" +
        "/Publish(StringParameter='Initial publish')");

    using (var pubReq = new HttpRequestMessage(HttpMethod.Post, publishUrl))
    {
        pubReq.Headers.TryAddWithoutValidation("X-RequestDigest", digest);
        using var pubResp = await http.SendAsync(pubReq);
        Console.WriteLine($"[Publish] HTTP {(int)pubResp.StatusCode} {pubResp.ReasonPhrase}");
        pubResp.EnsureSuccessStatusCode();
    }

    var fullPageUrl = new Uri(baseUri, serverRelativePage);
    Console.WriteLine($"✅ Modern page created & published: {fullPageUrl}");
}
