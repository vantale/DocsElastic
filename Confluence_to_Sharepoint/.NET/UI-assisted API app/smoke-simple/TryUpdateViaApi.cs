// Replaces the page canvas with a single-column section and one Text web part containing your HTML.
// pageServerRelativePath example: "/sites/TeamX/SitePages/MyPage.aspx"
// accessToken: Bearer token (Entra) with at least Sites.ReadWrite.All (delegated) or equivalent app perm.
static async Task UpdateModernPageHtmlWithRestAsync(
    string siteUrl,
    string pageServerRelativePath,
    string htmlContent,
    string accessToken,
    bool publish = true)
{
    var baseUri = new Uri(siteUrl.TrimEnd('/'));
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    http.DefaultRequestHeaders.Add("Accept", "application/json;odata=nometadata");

    // 1) Get digest
    var ctxUrl = new Uri(baseUri, "/_api/contextinfo");
    string digest;
    using (var ctxResp = await http.PostAsync(ctxUrl, new StringContent("")))
    {
        ctxResp.EnsureSuccessStatusCode();
        var ctxBody = await ctxResp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(ctxBody);
        var root = doc.RootElement;
        if (root.TryGetProperty("FormDigestValue", out var v) && v.ValueKind == JsonValueKind.String)
            digest = v.GetString()!;
        else if (root.TryGetProperty("d", out var d) &&
                 d.TryGetProperty("GetContextWebInformation", out var info) &&
                 info.TryGetProperty("FormDigestValue", out var v2) && v2.ValueKind == JsonValueKind.String)
            digest = v2.GetString()!;
        else
            throw new InvalidOperationException("Could not read FormDigestValue.");
    }

    // 2) Build a minimal modern canvas with one Text web part
    // Text web part well-known ID on modern pages:
    const string TextWebPartId = "d1d91016-032f-456d-98a4-721247c305e8";

    // Minimal, valid Client-Side Canvas JSON for a one-column section with one text control
    // This replaces the entire page content. If you want to append instead, you’d first GET CanvasContent1,
    // parse JSON, push another control into the last column, then PUT it back.
    var canvas = new
    {
        // version can vary; SharePoint tolerates a range. Keep it simple.
        // A simple one-section/one-column layout containing a text part:
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
                                        // Modern text web part accepts HTML here; SPO will sanitize disallowed bits.
                                        Text = htmlContent
                                    },
                                    serverProcessedContent = new {
                                        // Helpful for images/links if needed; can be empty for simple text/HTML.
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

    var canvasJson = JsonSerializer.Serialize(canvas);

    // 3) PUT CanvasContent1 via ListItemAllFields MERGE
    var fieldsUrl = new Uri(baseUri,
        $"/_api/web/GetFileByServerRelativePath(decodedurl='{Uri.EscapeDataString(pageServerRelativePath)}')/ListItemAllFields");

    var body = new
    {
        // IMPORTANT: Property name must be exactly CanvasContent1
        CanvasContent1 = canvasJson,
        // Optional: ensure modern layout
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
            $"/_api/web/GetFileByServerRelativePath(decodedurl='{Uri.EscapeDataString(pageServerRelativePath)}')/Publish(StringParameter='Programmatic update')");
        using var pubReq = new HttpRequestMessage(HttpMethod.Post, publishUrl);
        pubReq.Headers.TryAddWithoutValidation("X-RequestDigest", digest);
        using var pubResp = await http.SendAsync(pubReq);
        Console.WriteLine($"[Publish] HTTP {(int)pubResp.StatusCode} {pubResp.ReasonPhrase}");
        pubResp.EnsureSuccessStatusCode();
    }

    Console.WriteLine($"✅ Page updated: {new Uri(baseUri, pageServerRelativePath)}");
}
