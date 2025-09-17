// Minimal decoder: we only care about a few common flags
[Flags]
enum SpPerm { None=0, ViewListItems=1<<0, AddListItems=1<<1, EditListItems=1<<2, DeleteListItems=1<<3 }

static SpPerm DecodeEffectiveBasePermissions(ulong high, ulong low)
{
    SpPerm p = SpPerm.None;
    if ((low & (1UL<<0)) != 0) p |= SpPerm.ViewListItems;
    if ((low & (1UL<<1)) != 0) p |= SpPerm.AddListItems;
    if ((low & (1UL<<2)) != 0) p |= SpPerm.EditListItems;
    if ((low & (1UL<<3)) != 0) p |= SpPerm.DeleteListItems;
    return p;
}

static async Task CheckSitePagesPermissionsAsync(HttpClient http, Uri baseUri)
{
    var url = new Uri(baseUri, "/_api/web/lists/getByTitle('Site Pages')/EffectiveBasePermissions");
    using var resp = await http.GetAsync(url);
    Console.WriteLine($"[Site Pages perms] HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
    if (!resp.IsSuccessStatusCode)
    {
        Console.WriteLine("  ↳ Cannot read library perms (likely no Read on Site Pages or wrong site).");
        return;
    }

    var json = await resp.Content.ReadAsStringAsync();
    using var doc = System.Text.Json.JsonDocument.Parse(json);
    var e = doc.RootElement.TryGetProperty("d", out var d) ? d : doc.RootElement;
    ulong high = ulong.Parse(e.GetProperty("High").GetString() ?? "0");
    ulong low  = ulong.Parse(e.GetProperty("Low").GetString()  ?? "0");
    var perms  = DecodeEffectiveBasePermissions(high, low);

    Console.WriteLine($"  ↳ Effective perms: {perms}");
    Console.WriteLine(perms.HasFlag(SpPerm.ViewListItems)
        ? "  ✅ You can READ Site Pages."
        : "  ❌ You CANNOT READ Site Pages (404s are expected).");
    Console.WriteLine(perms.HasFlag(SpPerm.EditListItems)
        ? "  ✅ You can EDIT items (MERGE CanvasContent1 should work)."
        : "  ❌ You CANNOT EDIT items (MERGE will fail).");
}

static async Task PrintCurrentUserAsync(HttpClient http, Uri baseUri)
{
    using var r = await http.GetAsync(new Uri(baseUri, "/_api/web/currentuser"));
    Console.WriteLine($"[currentuser] HTTP {(int)r.StatusCode} {r.ReasonPhrase}");
    if (r.IsSuccessStatusCode) Console.WriteLine(await r.Content.ReadAsStringAsync());
}
