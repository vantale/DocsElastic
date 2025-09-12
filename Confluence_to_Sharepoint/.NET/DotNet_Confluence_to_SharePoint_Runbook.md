# Confluence → SharePoint Online (Modern) — **Pure .NET** Migration Runbook

This guide uses a **.NET 8** console app with **PnP Core SDK** (no PowerShell) to migrate Confluence pages to **SharePoint Online modern pages**.

> The app creates modern pages, uploads attachments, rewrites links, sets labels, and publishes — all in .NET. Use with the sample provided earlier (`ConfluenceToSPO.NetSample.zip`).

---

## 1) Prerequisites

- **.NET 8 SDK** on your workstation.
- **Site Owner** rights on the target SharePoint site.
- **Entra ID app registration** (public client / native):
  1. Entra admin center → **App registrations** → *New registration* → “Accounts in this org”.
  2. Add **Redirect URI**: `http://localhost` and mark as **Public client**.
  3. API permissions (delegated; admin consent may be required):
     - **Microsoft Graph**: `Sites.ReadWrite.All` (and optionally `Files.ReadWrite.All` if you prefer Graph for files).
     - **SharePoint**: `AllSites.FullControl` (or ensure your user is Site Owner; PnP Core honors site permissions).
  4. Copy the **Application (client) ID** and **Tenant ID**.

---

## 2) Get the sample & configure it

1. Download and unzip the sample: **ConfluenceToSPO.NetSample.zip** (from our chat).
2. Open `src/appsettings.json` and set:
   - `SiteUrl`: e.g. `https://contoso.sharepoint.com/sites/Knowledge`
   - `ManifestPath`: path to your transformer’s `manifest.json` (see step 4)
   - `AttachmentsLibraryTitle`: usually `Documents`
   - `AttachmentsFolder`: e.g. `ConfluenceAttachments`
   - `UseEnterpriseKeywords`: `true` if **Enterprise Keywords** is enabled on **Site Pages**; otherwise `false`
   - Under `PnPCore:Credentials:Default`: set your `ClientId`, `TenantId` (keep `RedirectUri` = `http://localhost`)
3. Test connectivity & auth:
   ```bash
   cd src
   dotnet restore
   dotnet run
   ```
   You should get an interactive sign-in and the app should start without connection errors.

---

## 3) Export from Confluence

- In Confluence: **Space → Export → XML (Full)** and include **attachments & comments**.
- Unzip the export (you should have `entities.xml` and an `attachments/` folder).

---

## 4) Transform Confluence XML → HTML + Manifest (Pure .NET)

The importer expects a **manifest** plus one **HTML** per page.

**What your transform should do (C#):**
- Parse `entities.xml`.
- For each page, collect: `id`, `title`, **storage body** (to HTML).
- Flatten a few macros (e.g., expand/toc/code) to simple HTML.
- Extract **labels** and **comments** (append comments to page HTML).
- Write each page to `out/pages/<slug>-<id>.html`.
- Write `out/manifest.json` with this shape:

```json
{
  "pages": [
    {
      "id": "12345",
      "title": "My Page",
      "slug": "my-page",
      "htmlPath": "../out/pages/my-page-12345.html",
      "labels": ["howto", "policy"],
      "attachments": [],
      "originalLinks": []
    }
  ]
}
```

> Keep slugs URL-safe. Store output near the solution, e.g., `/out`.  
> If you want, this transform can be a second command in the same .NET console (e.g., `dotnet run -- transform`).

---

## 5) Run the migration (modern pages)

With `manifest.json` and HTML files ready:

```bash
cd src
dotnet run
```

**What the app does (PnP Core SDK):**
1. **Creates** an empty modern page per manifest entry (or reuses existing) to get the final SharePoint page URLs.
2. **Uploads attachments** referenced in each HTML to:
   ```
   /<Documents>/<ConfluenceAttachments>/<slug>/...
   ```
   (overwrite enabled for idempotence).
3. **Rewrites links**:
   - Confluence page links like `...pageId=12345...` → the new **SharePoint modern page URLs**.
   - Confluence attachments like `/download/attachments/.../file.ext` → the newly uploaded **SharePoint file URLs**.
4. **Writes HTML** into a single **Text** web part and **publishes** the page.
5. **Sets labels**:
   - If `UseEnterpriseKeywords=true`, writes to the `TaxKeyword` field (or your configured internal name).
   - Otherwise writes to a custom text field named `ConfLabels` (create it on **Site Pages** if you choose this path).

---

## 6) Validate

- Open several migrated pages:
  - Formatting (headings, tables, lists) looks right.
  - Images load; attachment links download.
  - Intra-wiki links navigate to the correct new pages.
  - Labels appear in page properties.
  - “Imported comments” section is present.
- Adjust macro handling in your **transform** and re-run as needed (the importer is idempotent).

---

## 7) Rollout plan

- **Pilot** one space → one site; tweak transform rules/macros.
- Decide on navigation: **metadata-driven** (labels/keywords) or **hub/site navigation**.
- Batch spaces: run per space with a fresh export + transform.
- Keep a **page ID → new URL** CSV for communications/redirects.

---

## Common gotchas & fixes

- **403 / insufficient privileges**  
  Ensure your user is **Site Owner**. If your tenant requires admin consent for delegated scopes, ask for consent to Graph `Sites.ReadWrite.All` and/or SharePoint `AllSites.FullControl`.

- **Enterprise Keywords not visible**  
  On **Site Pages** → *Library settings* → **Enterprise Metadata and Keywords Settings** → enable.  
  Or set `UseEnterpriseKeywords=false` and use the `ConfLabels` text column.

- **Attachments not found**  
  The sample looks for an `attachments/` folder near the HTML (searches up to 5 levels). Adjust the path logic or place the files accordingly.

---

### Notes

- For richer layouts, add more sections/controls instead of a single Text part (`page.Sections.Add(...)`).
- If your Confluence export uses **pretty display links** (not `pageId`), add a transform pass that maps titles/ancestors to IDs and injects placeholders the C# replacer understands.
- MFA is supported — interactive auth will show a browser/device flow automatically.

---

**Ready to go?** Configure `appsettings.json`, produce `manifest.json` + HTMLs, and run `dotnet run`. 🚀
