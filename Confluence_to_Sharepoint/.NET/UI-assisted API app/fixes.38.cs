// Walk the entire page library (/SitePages or /Pages) recursively to locate the real file path.
// Returns the resolved ServerRelativeUrl, or null if not found.
static async Task<string?> ProbeAndResolveActualPathAsync(HttpClient http, Uri baseUri, string candidatePath)
{
    static string Normalize(string raw)
    {
        var p = Uri.UnescapeDataString(raw ?? string.Empty).Trim();
        if (!p.StartsWith("/")) p = "/" + p;
        while (p.EndsWith(";") || p.EndsWith(" ")) p = p[..^1];
        p = "/" + string.Join("/", p.Split('/', StringSplitOptions.RemoveEmptyEntries));
        return p;
    }
    static string EscOData(string s) => (s ?? string.Empty).Replace("'", "''");

    // Quick confirm: if the candidate already exists, use it.
    async Task<bool> ExistsAsync(string sru)
    {
        var byPath = new Uri(baseUri, $"/_api/web/GetFileByServerRelativePath(decodedurl='{Uri.EscapeDataString(sru)}')?$select=UniqueId");
        using (var r = await http.GetAsync(byPath))
        {
            Console.WriteLine($"[Probe A:ByPath] {sru} → {(int)r.StatusCode} {r.ReasonPhrase}");
            if (r.IsSuccessStatusCode) return true;
            if (r.StatusCode != System.Net.HttpStatusCode.NotFound) r.EnsureSuccessStatusCode();
        }
        var byUrl = new Uri(baseUri, $"/_api/web/GetFileByServerRelativeUrl('{EscOData(sru)}')?$select=UniqueId");
        using (var r = await http.GetAsync(byUrl))
        {
            Console.WriteLine($"[Probe B:ByUrl ] {sru} → {(int)r.StatusCode} {r.ReasonPhrase}");
            if (r.IsSuccessStatusCode) return true;
            if (r.StatusCode != System.Net.HttpStatusCode.NotFound) r.EnsureSuccessStatusCode();
        }
        return false;
    }

    // Normalize inputs
    candidatePath = Normalize(candidatePath);

    // Extract library root (…/SitePages or …/Pages)
    var idx = candidatePath.IndexOf("/SitePages/", StringComparison.OrdinalIgnoreCase);
    var libSeg = "/SitePages";
    if (idx < 0)
    {
        idx = candidatePath.IndexOf("/Pages/", StringComparison.OrdinalIgnoreCase);
        libSeg = "/Pages";
    }
    if (idx < 0)
    {
        Console.Error.WriteLine("⚠️ Candidate path does not contain /SitePages/ or /Pages/.");
        return null;
    }
    var libRoot = candidatePath[..idx] + libSeg;              // e.g., /sites/TeamX/SitePages
    var leaf    = candidatePath[(candidatePath.LastIndexOf('/') + 1)..]; // e.g., First-Test-Page.aspx

    // If candidate already exists, we’re done.
    if (await ExistsAsync(candidatePath)) return candidatePath;

    // Build match keys (tolerant to hyphens/spaces/suffixes)
    string LeafName(string p) => System.IO.Path.GetFileName(p);
    string Stem(string name)  => System.IO.Path.GetFileNameWithoutExtension(name);
    string Canon(string s)    => new string((s ?? "").ToLowerInvariant().Where(ch => char.IsLetterOrDigit(ch)).ToArray());

    var targetLeaf    = LeafName(candidatePath);           // First-Test-Page.aspx
    var targetStem    = Stem(targetLeaf);                  // First-Test-Page
    var targetCanon   = Canon(targetStem);                 // firsttestpage
    var targetStemAlt = targetStem.Replace('-', ' ').Trim();

    Console.WriteLine($"[Resolver] Library root: '{libRoot}', target leaf='{targetLeaf}', stem='{targetStem}'");

    // BFS across the library folders
    var q = new Queue<string>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    q.Enqueue(libRoot);

    int explored = 0;
    const int MAX_FOLDERS = 5000;

    while (q.Count > 0 && explored < MAX_FOLDERS)
    {
        var folder = Normalize(q.Dequeue());
        if (!seen.Add(folder)) continue;
        explored++;
        Console.WriteLine($"  [BFS] Exploring {folder}");

        // List files in this folder (large $top to minimize paging needs)
        var filesUrl = new Uri(baseUri,
            $"/_api/web/GetFolderByServerRelativePath(decodedurl='{Uri.EscapeDataString(folder)}')/Files" +
            "?$select=Name,ServerRelativeUrl,TimeLastModified&$top=1000");
        using (var fr = await http.GetAsync(filesUrl))
        {
            if (fr.IsSuccessStatusCode)
            {
                var json = await fr.Content.ReadAsStringAsync();
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                System.Text.Json.JsonElement arr;

                bool got = false;
                if (doc.RootElement.TryGetProperty("value", out arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array) got = true;
                else if (doc.RootElement.TryGetProperty("d", out var dEl) &&
                         dEl.TryGetProperty("results", out arr) &&
                         arr.ValueKind == System.Text.Json.JsonValueKind.Array) got = true;

                if (got)
                {
                    // Try exact leaf first
                    for (int i = 0; i < arr.GetArrayLength(); i++)
                    {
                        var name = arr[i].GetProperty("Name").GetString() ?? "";
                        if (string.Equals(name, targetLeaf, StringComparison.OrdinalIgnoreCase))
                        {
                            var sru = arr[i].GetProperty("ServerRelativeUrl").GetString();
                            Console.WriteLine($"  [Hit exact] {name} → {sru}");
                            return sru;
                        }
                    }
                    // Then fuzzy (contains / canonical contains)
                    for (int i = 0; i < arr.GetArrayLength(); i++)
                    {
                        var name = arr[i].GetProperty("Name").GetString() ?? "";
                        var stem = Stem(name);
                        if (stem.Contains(targetStem, StringComparison.OrdinalIgnoreCase) ||
                            stem.Contains(targetStemAlt, StringComparison.OrdinalIgnoreCase) ||
                            Canon(stem).Contains(targetCanon, StringComparison.OrdinalIgnoreCase))
                        {
                            var sru = arr[i].GetProperty("ServerRelativeUrl").GetString();
                            Console.WriteLine($"  [Hit fuzzy] {name} → {sru}");
                            return sru;
                        }
                    }
                }
                else
                {
                    Console.WriteLine("    [Files] Unexpected JSON (no array).");
                }
            }
            else if (fr.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // folder might be trimmed; continue
            }
            else
            {
                fr.EnsureSuccessStatusCode();
            }
        }

        // Enqueue subfolders (skip Forms)
        var foldersUrl = new Uri(baseUri,
            $"/_api/web/GetFolderByServerRelativePath(decodedurl='{Uri.EscapeDataString(folder)}')/Folders?$select=Name,ServerRelativeUrl&$top=500");
        using (var r = await http.GetAsync(foldersUrl))
        {
            if (r.IsSuccessStatusCode)
            {
                var json = await r.Content.ReadAsStringAsync();
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                System.Text.Json.JsonElement arr;

                bool got = false;
                if (doc.RootElement.TryGetProperty("value", out arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array) got = true;
                else if (doc.RootElement.TryGetProperty("d", out var dEl) &&
                         dEl.TryGetProperty("results", out arr) &&
                         arr.ValueKind == System.Text.Json.JsonValueKind.Array) got = true;

                if (got)
                {
                    for (int i = 0; i < arr.GetArrayLength(); i++)
                    {
                        var name = arr[i].GetProperty("Name").GetString() ?? "";
                        if (string.Equals(name, "Forms", StringComparison.OrdinalIgnoreCase)) continue;
                        var sru = arr[i].GetProperty("ServerRelativeUrl").GetString();
                        if (!string.IsNullOrWhiteSpace(sru))
                        {
                            var norm = Normalize(sru);
                            if (!seen.Contains(norm)) q.Enqueue(norm);
                        }
                    }
                }
            }
        }
    }

    Console.WriteLine($"  [BFS] Explored {explored} folders under {libRoot} (max {MAX_FOLDERS}).");
    return null;
}
