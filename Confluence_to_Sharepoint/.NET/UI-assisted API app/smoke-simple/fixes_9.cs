// Tries multiple shapes to resolve the Site Pages folder, with a verified fallback
static async Task<string> ResolveSitePagesFolderAsync(HttpClient http, Uri baseUri)
{
    // Try #1: expand RootFolder on the list (works in many tenants)
    var try1 = new Uri(baseUri, "/_api/web/lists/getByTitle('Site Pages')?$select=RootFolder/ServerRelativeUrl&$expand=RootFolder");
    using (var r1 = await http.GetAsync(try1))
    {
        Console.WriteLine($"[List expand RootFolder] HTTP {(int)r1.StatusCode} {r1.ReasonPhrase}");
        if (r1.IsSuccessStatusCode)
        {
            var body1 = await r1.Content.ReadAsStringAsync();
            using var j1 = JsonDocument.Parse(body1);
            if (j1.RootElement.TryGetProperty("RootFolder", out var rf1) &&
                rf1.TryGetProperty("ServerRelativeUrl", out var sru1) &&
                sru1.ValueKind == JsonValueKind.String)
            {
                var path = sru1.GetString();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    if (!path!.StartsWith("/")) path = "/" + path;
                    return path!;
                }
            }
            // verbose shape: { "d": { "RootFolder": { "ServerRelativeUrl": "..." } } }
            if (j1.RootElement.TryGetProperty("d", out var d1) &&
                d1.TryGetProperty("RootFolder", out var rf1v) &&
                rf1v.TryGetProperty("ServerRelativeUrl", out var sru1v) &&
                sru1v.ValueKind == JsonValueKind.String)
            {
                var path = sru1v.GetString();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    if (!path!.StartsWith("/")) path = "/" + path;
                    return path!;
                }
            }
        }
    }

    // Try #2: hit RootFolder entity directly and select the field
    var try2 = new Uri(baseUri, "/_api/web/lists/getByTitle('Site Pages')/RootFolder?$select=ServerRelativeUrl");
    using (var r2 = await http.GetAsync(try2))
    {
        Console.WriteLine($"[RootFolder?$select=ServerRelativeUrl] HTTP {(int)r2.StatusCode} {r2.ReasonPhrase}");
        if (r2.IsSuccessStatusCode)
        {
            var body2 = await r2.Content.ReadAsStringAsync();
            using var j2 = JsonDocument.Parse(body2);
            // Accept both: {"ServerRelativeUrl":"..."} OR {"value":"..."} OR verbose
            string? path =
                (j2.RootElement.TryGetProperty("ServerRelativeUrl", out var p1) && p1.ValueKind == JsonValueKind.String) ? p1.GetString() :
                (j2.RootElement.TryGetProperty("value", out var p2) && p2.ValueKind == JsonValueKind.String) ? p2.GetString() :
                (j2.RootElement.TryGetProperty("d", out var dv) &&
                 dv.TryGetProperty("ServerRelativeUrl", out var p3) && p3.ValueKind == JsonValueKind.String) ? p3.GetString() :
                null;

            if (!string.IsNullOrWhiteSpace(path))
            {
                if (!path!.StartsWith("/")) path = "/" + path;
                return path!;
            }
            Console.Error.WriteLine("[RootFolder] Unexpected JSON (first 400 chars): " + body2[..Math.Min(body2.Length, 400)]);
        }
    }

    // Try #3 (fallback): derive from contextinfo → WebFullUrl/SiteFullUrl + "/SitePages", then verify
    var ctxUrl = new Uri(baseUri, "/_api/contextinfo");
    using (var ctx = await http.PostAsync(ctxUrl, new StringContent("")))
    {
        Console.WriteLine($"[contextinfo for fallback] HTTP {(int)ctx.StatusCode} {ctx.ReasonPhrase}");
        ctx.EnsureSuccessStatusCode();
        var body = await ctx.Content.ReadAsStringAsync();
        using var j = JsonDocument.Parse(body);
        var root = j.RootElement;

        string? full = null;
        // nometadata
        if (root.TryGetProperty("WebFullUrl", out var wfu) && wfu.ValueKind == JsonValueKind.String) full = wfu.GetString();
        if (full is null && root.TryGetProperty("SiteFullUrl", out var sfu) && sfu.ValueKind == JsonValueKind.String) full = sfu.GetString();
        // verbose
        if (full is null && root.TryGetProperty("d", out var dv) &&
            dv.TryGetProperty("GetContextWebInformation", out var info))
        {
            if (info.TryGetProperty("WebFullUrl", out var w2) && w2.ValueKind == JsonValueKind.String) full = w2.GetString();
            if (full is null && info.TryGetProperty("SiteFullUrl", out var s2) && s2.ValueKind == JsonValueKind.String) full = s2.GetString();
        }

        if (string.IsNullOrWhiteSpace(full))
            throw new InvalidOperationException("Fallback failed: contextinfo did not include WebFullUrl/SiteFullUrl.");

        var fullUri = new Uri(full!, UriKind.Absolute);
        var folderGuess = (fullUri.AbsolutePath.TrimEnd('/') + "/SitePages");
        if (!folderGuess.StartsWith("/")) folderGuess = "/" + folderGuess;

        // Verify the folder exists & is accessible
        var check = new Uri(baseUri, $"/_api/web/GetFolderByServerRelativeUrl('{folderGuess}')");
        using var rc = await http.GetAsync(check);
        Console.WriteLine($"[Verify guessed folder '{folderGuess}'] HTTP {(int)rc.StatusCode} {rc.ReasonPhrase}");
        if (rc.IsSuccessStatusCode) return folderGuess;

        throw new InvalidOperationException(
            $"Could not resolve Site Pages folder. Last guess '{folderGuess}' → HTTP {(int)rc.StatusCode}.");
    }
}
