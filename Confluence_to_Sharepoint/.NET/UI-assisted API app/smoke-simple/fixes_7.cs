// Pull a string property from OData responses in multiple shapes (nometadata/minimal/verbose)
static string? ExtractString(JsonElement root, params string[] names)
{
    // nometadata: {"value":"..."} or {"ServerRelativeUrl":"..."}
    foreach (var n in names)
        if (root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();

    // verbose: {"d":{"ServerRelativeUrl":"..."}}
    if (root.TryGetProperty("d", out var d))
        foreach (var n in names)
            if (d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();

    return null;
}

// Resolve the Site Pages server-relative folder path reliably
static async Task<string> ResolveSitePagesFolderAsync(HttpClient http, Uri baseUri)
{
    // Confirm list is readable (helps diagnose 403 early)
    var listMetaUrl = new Uri(baseUri, "/_api/web/lists/getByTitle('Site Pages')?$select=Title,HasUniqueRoleAssignments");
    using (var lm = await http.GetAsync(listMetaUrl))
    {
        Console.WriteLine($"[List meta] HTTP {(int)lm.StatusCode} {lm.ReasonPhrase}");
        if (!lm.IsSuccessStatusCode)
            throw new InvalidOperationException($"Cannot read 'Site Pages' list (HTTP {(int)lm.StatusCode}). You likely need at least Read/Contribute.");
    }

    // Ask for the folder entity and select the field → predictable JSON
    var rootUrlReq = new Uri(baseUri, "/_api/web/lists/getByTitle('Site Pages')/RootFolder?$select=ServerRelativeUrl");
    using var rf = await http.GetAsync(rootUrlReq);
    Console.WriteLine($"[RootFolder?$select=ServerRelativeUrl] HTTP {(int)rf.StatusCode} {rf.ReasonPhrase}");
    rf.EnsureSuccessStatusCode();

    var body = await rf.Content.ReadAsStringAsync();
    using var j = JsonDocument.Parse(body);
    var root = j.RootElement;

    // Accept both shapes: {"ServerRelativeUrl":"..."} OR {"value":"..."} OR verbose.
    var sitePagesFolder = ExtractString(root, "ServerRelativeUrl") ?? ExtractString(root, "value");
    if (string.IsNullOrWhiteSpace(sitePagesFolder))
        throw new InvalidOperationException("Could not read ServerRelativeUrl from RootFolder (tried 'ServerRelativeUrl' and 'value').");

    return sitePagesFolder!;
}
