// 1) Normalize any incoming server-relative path (handles your "%2F..." case)
static string NormalizeServerRelativePath(string raw)
{
    var p = Uri.UnescapeDataString(raw ?? string.Empty).Trim();
    if (!p.StartsWith("/")) p = "/" + p;
    // Remove accidental trailing punctuation/spaces
    while (p.EndsWith(";") || p.EndsWith(" ")) p = p[..^1];
    // Collapse duplicate slashes (except the leading one)
    p = "/" + string.Join("/", p.Split('/', StringSplitOptions.RemoveEmptyEntries));
    return p;
}

// 2) Build robust endpoints (use ByServerRelativeUrl; escape single quotes only)
static (Uri fieldsUrl, Uri publishUrl, Uri probeUrl) BuildFileEndpoints(Uri baseUri, string serverRelativePath)
{
    var safe = serverRelativePath.Replace("'", "''");
    var fields  = new Uri(baseUri, $"/_api/web/GetFileByServerRelativeUrl('{safe}')/ListItemAllFields");
    var publish = new Uri(baseUri, $"/_api/web/GetFileByServerRelativeUrl('{safe}')/Publish(StringParameter='Smoke update')");
    var probe   = new Uri(baseUri, $"/_api/web/GetFileByServerRelativeUrl('{safe}')?$select=Name,Exists,ServerRelativeUrl");
    return (fields, publish, probe);
}


// Normalize first to strip any %2F etc.
var normalizedPath = NormalizeServerRelativePath(pageServerRelativePath);

// Use the ByServerRelativeUrl variant (no percent-encoding)
var (fieldsUrl, publishUrl, probeUrl) = BuildFileEndpoints(baseUri, normalizedPath);

// Optional: sanity probe before MERGE (helps pinpoint bad paths)
using (var probe = await http.GetAsync(probeUrl))
{
    Console.WriteLine($"[Probe file exists] {normalizedPath} → HTTP {(int)probe.StatusCode} {probe.ReasonPhrase}");
    if (!probe.IsSuccessStatusCode)
        throw new InvalidOperationException($"File not found/accessible at '{normalizedPath}'. Check the site scope and path.");
}

