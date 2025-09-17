// Finds a modern page's server-relative path by scanning page libraries (119/850) recursively.
// No dependency besides HttpClient; logs progress as it explores folders.
static async Task<string> ResolvePagePathAcrossLibrariesAsync(HttpClient http, System.Uri baseUri, string hint)
{
    // ----- helpers -----
    static string Esc(string s) => s.Replace("'", "''"); // OData single-quote escape

    static string Normalize(string raw)
    {
        var p = System.Uri.UnescapeDataString(raw ?? string.Empty).Trim();
        if (!p.StartsWith("/")) p = "/" + p;
        while (p.EndsWith(";") || p.EndsWith(" ")) p = p[..^1];
        p = "/" + string.Join("/", p.Split('/', StringSplitOptions.RemoveEmptyEntries));
        return p;
    }

    // Accept either a plain array or an object with { results: [...] }
    static bool TryGetArray(System.Text.Json.JsonElement el, out System.Text.Json.JsonElement arr)
    {
        if (el.ValueKind == System.Text.Json.JsonValueKind.Array) { arr = el; return true; }
        if (el.ValueKind == System.Text.Json.JsonValueKind.Object &&
            el.TryGetProperty("results", out var res) &&
            res.ValueKind == System.Text.Json.JsonValueKind.Array)
        { arr = res; return true; }
        arr = default; return false;
    }
    // ----- end helpers -----

    var isFile   = hint.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase);
    var fileLeaf = isFile ? hint : (hint.Replace(' ', '-') + ".aspx");
    var stem     = isFile ? System.IO.Path.GetFileNameWithoutExtension(hint) : hint.Replace(' ', '-');

    // 1) Get candidate page libraries: 119 = Site Pages, 850 = Publishing "Pages"
    var listsUrl = new System.Uri(
        baseUri,
        "/_api/web/lists" +
        "?$select=Id,Title,BaseTemplate,RootFolder/ServerRelativeUrl" +
        "&$expand=RootFolder" +
        "&$filter=(BaseTemplate eq 119) or (BaseTemplate eq 850)");

    using var lr = await http.GetAsync(listsUrl);
    Console.WriteLine($"[Lists by template 119/850] HTTP {(int)lr.StatusCode} {lr.ReasonPhrase}");
    lr.EnsureSuccessStatusCode();

    var listsJson = await lr.Content.ReadAsStringAsync();
    using var listsDoc = System.Text.Json.JsonDocument.Parse(listsJson);

    // Extract array (supports both OData shapes)
    System.Text.Json.JsonElement listsArr;
    if (listsDoc.RootElement.TryGetProperty("value", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Array)
        listsArr = v;
    else if (listsDoc.RootElement.TryGetProperty("d", out var d) &&
             d.TryGetProperty("results", out var r) &&
             r.ValueKind == System.Text.Json.JsonValueKind.Array)
        listsArr = r;
    else
        throw new InvalidOperationException("Could not read lists array for templates 119/850.");

    for (int i = 0; i < listsArr.GetArrayLength(); i++)
    {
        var el           = listsArr[i];
        var listTitle    = el.TryGetProperty("Title", out var t) ? t.GetString() : "(no title)";
        var baseTemplate = el.GetProperty("BaseTemplate").GetInt32();
        var rootFolder   = el.GetProperty("RootFolder").GetProperty("ServerRelativeUrl").GetString() ?? "/SitePages";

        var root = Normalize(rootFolder);
        Console.WriteLine($"[List] {listTitle} (template {baseTemplate}) root='{root}'");

        // 2) BFS through folders under the page library
        var toVisit = new Queue<string>();
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        toVisit.Enqueue(root);

        int explored = 0;
        const int MAX_FOLDERS = 500; // safety cap

        while (toVisit.Count > 0 && explored < MAX_FOLDERS)
        {
            var folder = toVisit.Dequeue();
            if (!seen.Add(folder)) continue;
            explored++;
            Console.WriteLine($"  [BFS] Exploring {folder}");

            // 2a) Look for the exact file name in this folder
            var filesExact = new System.Uri(
                baseUri,
                $"/_api/web/GetFolderByServerRelativeUrl('{Esc(folder)}')/Files" +
                $"?$select=Name,ServerRelativeUrl&$filter=Name eq '{Esc(fileLeaf)}'&$top=1");

            using (var fx = await http.GetAsync(filesExact))
            {
                if (fx.IsSuccessStatusCode)
                {
                    var json = await fx.Content.ReadAsStringAsync();
                    using var doc = System.Text.Json.JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("value", out var arr1) && arr1.ValueKind == System.Text.Json.JsonValueKind.Array && arr1.GetArrayLength() > 0)
                    {
                        var sru = arr1[0].GetProperty("ServerRelativeUrl").GetString()!;
                        Console.WriteLine($"  [Hit exact] {sru}");
                        return Normalize(sru);
                    }
                    if (doc.RootElement.TryGetProperty("d", out var d1) &&
                        d1.TryGetProperty("results", out var arr1v) &&
                        arr1v.ValueKind == System.Text.Json.JsonValueKind.Array &&
                        arr1v.GetArrayLength() > 0)
                    {
                        var sru = arr1v[0].GetProperty("ServerRelativeUrl").GetString()!;
                        Console.WriteLine($"  [Hit exact] {sru}");
                        return Normalize(sru);
                    }
                }
            }

            // 2b) Heuristic: contains the stem (helps when SharePoint appended "-1.aspx", etc.)
            var filesLike = new System.Uri(
                baseUri,
                $"/_api/web/GetFolderByServerRelativeUrl('{Esc(folder)}')/Files" +
                $"?$select=Name,ServerRelativeUrl&$filter=substringof('{Esc(stem)}',Name)&$top=5");

            using (var fl = await http.GetAsync(filesLike))
            {
                if (fl.IsSuccessStatusCode)
                {
                    var json = await fl.Content.ReadAsStringAsync();
                    using var doc = System.Text.Json.JsonDocument.Parse(json);

                    System.Text.Json.JsonElement arr;
                    if ((doc.RootElement.TryGetProperty("value", out arr) ||
                        (doc.RootElement.TryGetProperty("d", out var dv) && dv.TryGetProperty("results", out arr))) &&
                        arr.ValueKind == System.Text.Json.JsonValueKind.Array &&
                        arr.GetArrayLength() > 0)
                    {
                        var picked = arr[0];
                        var sru = picked.GetProperty("ServerRelativeUrl").GetString()!;
                        Console.WriteLine($"  [Hit contains] {picked.GetProperty("Name").GetString()} → {sru}");
                        return Normalize(sru);
                    }
                }
            }

            // 2c) Enqueue child folders (skip "Forms")
            var foldersUrl = new System.Uri(
                baseUri,
                $"/_api/web/GetFolderByServerRelativeUrl('{Esc(folder)}')/Folders?$select=Name,ServerRelativeUrl&$top=200");

            using (var fr = await http.GetAsync(foldersUrl))
            {
                if (fr.IsSuccessStatusCode)
                {
                    var json = await fr.Content.ReadAsStringAsync();
                    using var doc = System.Text.Json.JsonDocument.Parse(json);

                    System.Text.Json.JsonElement arr;
                    if ((doc.RootElement.TryGetProperty("value", out arr) ||
                        (doc.RootElement.TryGetProperty("d", out var dv) && dv.TryGetProperty("results", out arr))) &&
                        arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        for (int j = 0; j < arr.GetArrayLength(); j++)
                        {
                            var f = arr[j];
                            var name = f.TryGetProperty("Name", out var nEl) ? (nEl.GetString() ?? "") : "";
                            if (string.Equals(name, "Forms", StringComparison.OrdinalIgnoreCase)) continue;

                            var child = f.GetProperty("ServerRelativeUrl").GetString();
                            if (!string.IsNullOrWhiteSpace(child))
                            {
                                var norm = Normalize(child!);
                                if (!seen.Contains(norm)) toVisit.Enqueue(norm);
                            }
                        }
                    }
                }
            }
        }

        Console.WriteLine($"  [BFS] Explored {explored} folders under {root} (max {MAX_FOLDERS}).");
    }

    throw new InvalidOperationException($"Could not find a page matching '{hint}' in this site (tried folders recursively).");
}
