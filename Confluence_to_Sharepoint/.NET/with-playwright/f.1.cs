public async Task LaunchAsync()
{
    _playwright = await Microsoft.Playwright.Playwright.CreateAsync();

    // Fallback profile path if none provided
    var profile = string.IsNullOrWhiteSpace(_cfg.UserDataDir)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                       "CspUiImporter", "EdgeProfile")
        : _cfg.UserDataDir;

    Directory.CreateDirectory(profile);

    var opts = new BrowserTypeLaunchPersistentContextOptions
    {
        Channel = "msedge",
        Headless = _cfg.Headless,
        ViewportSize = new() { Width = 1400, Height = 900 }
    };

    _ctx = await _playwright.Chromium.LaunchPersistentContextAsync(profile, opts);
    _page = await _ctx.NewPageAsync();
}
