// Helper: extract FormDigestValue from contextinfo in any OData shape
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

// One-shot: diagnose perms, discover Site Pages path, create & publish a modern page
static async Task CreateModernPageWithDiagnosticsAsync(
    AppConfig cfg,
    IReadOnlyList<PwCookie> cookies,
    string pageNameSlug,
    string pageTitle)
{
    var baseUri = new Uri(cfg.TargetSiteUrl.TrimEnd('/'));

    // HttpClient with your captured cookies
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
        handler.CookieContainer.Add(baseUri, new Cookie(name, value, path, domain));
    }

    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    http.DefaultRequestHeaders.Add("Accept", "application/json;odata=nometadata");

    // A) Who am I? (helpful when debugging 403)
    var whoUrl = new Uri(baseUri, "/_api/web/currentuser");
    using (var who = await http.GetAsync(whoUrl))
    {
        Console.WriteLine($"[currentuser] HTTP {(int)who.StatusCode} {who.ReasonPhrase}");
        if (who.IsSuccessStatusCode)
            Console.WriteLine(await who.Content.ReadAsStringAsync());
    }

    // B) Site Pages list metadata → confirms at least Read & whether it breaks inheritance
    var listMetaUrl = new Uri(baseUri, "/_api/web/lists/getByTitle('Site Pages')?$select=Title,HasUniqueRoleAssignments");
    using (var lm = await http.GetAsync(listMetaUrl))
    {
        Console.WriteLine($"[List meta] HTTP {(int)lm.StatusCode} {lm.ReasonPhrase}");
        if (lm.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException(
                "403: You don't have even READ permission on 'Site Pages'. " +
                "Ask the site owner to grant you Contribute/Edit on the Site Pages library.");
        lm.EnsureSuccessStatusCode();
    }

    // C) Get the real server-relative path of Site Pages (avoids guessing /sites/... vs /teams/...)
    var rootUrlReq = new Uri(baseUri, "/_api/web/lists/getByTitle('Site Pages')/RootFolder/ServerRelativeUrl");
    string sitePagesFolder;
    using (var rf = await http.GetAsync(rootUrlReq))
    {
        Console.WriteLine($"[RootFolder.ServerRelativeUrl] HTTP {(int)rf.StatusCode} {rf.ReasonPhrase}");
        if (rf.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException(
                "403 on RootFolder: 'Site Pages' likely has unique permissions that exclude you. " +
                "Request Contribute/Edit on that library.");
        rf.EnsureSuccessStatusCode();

        using var j = JsonDocument.Parse(await rf.Content.ReadAsStringAsync());
        var root = j.RootElement;
        sitePagesFolder =
            (root.TryGetProperty("ServerRelativeUrl", out var sru) ? sru.GetString() :
            (root.TryGetProperty("d", out var dEl) && dEl.TryGetProperty("ServerRelativeUrl", out var sru2) ? sru2.GetString() : null))
            ?? throw new InvalidOperationException("Could not read ServerRelativeUrl from RootFolder.");
    }

    var pageRelPath = $"{sitePagesFolder.TrimEnd('/')}/{pageNameSlug}.aspx";
    Console.WriteLine($"[Paths] folder='{sitePagesFolder}' page='{pageRelPath}'");

    // D) Get digest (contextinfo)
    var ctxUrl = new Uri(baseUri, "/_api/contextinfo");
    string digest;
    using (var ctxResp = await http.PostAsync(ctxUrl, new StringContent("")))
    {
        Console.WriteLine($"[contextinfo] HTTP {(int)ctxResp.StatusCode} {ctxResp.ReasonPhrase}");
        ctxResp.EnsureSuccessStatusCode();
        var ctxBody = await ctxResp.Content.ReadAsStringAsync();
        digest = ExtractFormDigestValue(ctxBody);
    }

    if (string.IsNullOrWhiteSpace(digest))
        throw new InvalidOperationException("FormDigestValue was empty.");

    // E) Create modern page via AddTemplateFile (templateFileType=3)
    var addUrl = new Uri(baseUri,
        $"/_api/web/GetFolderByServerRelativeUrl('{sitePagesFolder}')/Files/AddTemplateFile(" +
        $"urlOfFile='{pageRelPath}',templateFileType=3)");

    using (var addReq = new HttpRequestMessage(HttpMethod.Post, addUrl))
    {
        addReq.Headers.TryAddWithoutValidation("X-RequestDigest", digest);
        using var addResp = await http.SendAsync(addReq);
        Console.WriteLine($"[AddTemplateFile] {addUrl} → HTTP {(int)addResp.StatusCode} {addResp.ReasonPhrase}");
        if (addResp.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException(
                "403 on AddTemplateFile: you lack ADD (Contribute/Edit) rights on 'Site Pages'.");
        addResp.EnsureSuccessStatusCode();
    }

    // F) Set fields (Title, PageLayoutType=Article)
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

    // G) Publish the page
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
