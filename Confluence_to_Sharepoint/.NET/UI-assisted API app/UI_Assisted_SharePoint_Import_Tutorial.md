# UI-Assisted SharePoint Import — Step-by-Step (.NET 8 + Playwright, **No Tenant Consent**)

This guide walks you through migrating Confluence pages to **SharePoint Online (modern pages)** using a **UI-assisted API** app.  
You sign in **once** in a real browser; the app then calls SharePoint’s REST endpoints **from your user session** — no Microsoft Entra (Azure AD) app consent needed.

---

## What you’ll use

- **App**: ConfluenceToSPO.UIAssisted.Net (download below)
- **How it works**: Playwright opens a real browser for login and saves `auth.json`. The importer uses that session to:
  1. Create/reuse modern pages in **Site Pages**  
  2. Upload attachments to your **Documents** library  
  3. Inject your HTML into a **Text** web part (Canvas JSON)  
  4. Publish the page  
  5. Set labels (Enterprise Keywords or a custom column)

---

## Prerequisites (no tenant admin required)

- **Permissions on the SharePoint site**: at least **Edit** on **Site Pages** and the attachments library.  
  If **content approval** is ON, you also need **Approve Items** (or temporarily turn it off).
- **.NET 8 SDK**
- **Playwright browsers** (installed once by the helper script in the project)
- **Inputs** from Confluence transform:
  - One **HTML file per page**
  - A **manifest.json** (see schema below)
  - Optional: an `attachments/` folder near each HTML (used to upload images/files)

---

## Files & structure you need

- `manifest.json` (sample below)
- Page HTMLs, e.g. `out/pages/<slug>-<id>.html`
- **Canvas template** — a one-time capture of the page JSON for a single **Text** web part with a token `{{HTML}}`

**Manifest schema (example):**
```json
{
  "pages": [
    {
      "id": "12345",
      "title": "Welcome",
      "slug": "welcome",
      "htmlPath": "../out/pages/welcome-12345.html",
      "labels": ["howto", "policy"]
    }
  ]
}
```

---

## 1) Download the app

Unzip the project somewhere you can run it:

**ConfluenceToSPO.UIAssisted.Net.zip** (see link in the chat message that accompanied this tutorial).

Project layout (key files):
```
src/
  Program.cs                 # login/import commands
  appsettings.json           # site URL, paths, fields
  canvasTemplate.json        # your captured Canvas JSON (with {{HTML}} token)
examples/
  manifest.sample.json
  canvasTemplate.README.md   # how to capture the template
```

---

## 2) Configure the app

Open `src/appsettings.json` and set:
- `SiteUrl`: e.g. `https://contoso.sharepoint.com/sites/Knowledge`
- `ManifestPath`: path to your manifest (e.g. `../out/manifest.json`)
- `AttachmentsLibraryTitle`: typically `Documents`
- `AttachmentsFolder`: e.g. `ConfluenceAttachments`
- `EnterpriseKeywordsFieldInternalName`: usually `TaxKeyword` (if using Enterprise Keywords)

Example:
```json
{
  "SiteUrl": "https://contoso.sharepoint.com/sites/Knowledge",
  "StorageStatePath": "auth.json",
  "UserDataDir": "C:\\Playwright\\Profile",
  "ManifestPath": "../out/manifest.json",
  "CanvasTemplatePath": "canvasTemplate.json",
  "AttachmentsLibraryTitle": "Documents",
  "AttachmentsFolder": "ConfluenceAttachments",
  "EnterpriseKeywordsFieldInternalName": "TaxKeyword"
}
```

---

## 3) Capture `canvasTemplate.json` (one time)

1. In SharePoint, create a **new modern page** with a **Text** web part. In the editor, type a unique token, e.g. `{{HTML}}`.
2. Open **DevTools → Network** and click **Publish**.
3. Find the request to: `/_api/sitepages/pages({id})/SavePageAsDraft` and copy the **request body JSON**.
4. Paste the JSON into `src/canvasTemplate.json`.  
5. Replace the text content where your token appears with **`{{HTML}}`**. Ensure the file remains **valid JSON** (no comments).

> The importer will replace `{{HTML}}` with your page HTML at runtime.

---

## 4) First run — install browsers & login

From the `src` folder:

```bash
dotnet build
pwsh ./bin/Debug/net8.0/playwright.ps1 install   # one-time browser install
dotnet run -- login                               # opens a real browser to your site
```

- Sign in (MFA/SSO ok).  
- When the page loads, the app saves **`auth.json`** (Playwright storage state).

> If the site uses Conditional Access, keep **headed** mode for login. You can later run headless for the `import` step.

---

## 5) Import pages

```bash
dotnet run -- import
```

What happens for each manifest entry:
1. **Create/reuse** `{slug}.aspx` in **Site Pages** and record its URL
2. **Upload attachments** referenced in HTML to `/Documents/ConfluenceAttachments/{slug}/`
3. **Rewrite links** (`pageId=` and `/download/attachments/`) to modern SPO URLs
4. **Apply Canvas** using your template with the final HTML and **Publish**
5. **Set labels** to Enterprise Keywords (`TaxKeyword`) or your chosen field

---

## 6) Validate & iterate

- Open a few migrated pages: check formatting, images, attachment links, labels, and intra-wiki links.
- If something looks off, adjust your **transform** (HTML/macros), fix paths, then re-run. The importer is **idempotent** (overwrites content & files).

---

## Troubleshooting

- **`canvasTemplate.json` still has placeholder**: Capture the real JSON as described in step 3.
- **401/403 when importing**: Your session expired or lacks rights. Re-run `dotnet run -- login`, or ensure you have **Edit** (and **Approve Items** if required).
- **Content approval blocks publishing**: Get **Approve Items** or temporarily disable it in **Site Pages** settings.
- **“Missing attachment” warnings**: Ensure files are under a nearby `attachments/` folder; the app searches up to 5 levels up.
- **Throttling (429/503)**: Slow down runs; consider batching pages per run.

---

## Security notes

- **`auth.json`** represents your login — treat it like a credential. Store securely; do not commit to source control.
- The app uses your **user session**, so it only does what you could do in the UI.
- You can delete `auth.json` after you’re done to force a fresh login next run.

---

## Why this avoids tenant consent

All REST calls are made **from your authenticated browser context**. That’s the same session the SharePoint UI uses, so **no Entra app** or tenant-wide **admin consent** is necessary.

---

## Appendix — Manifest schema

```json
{
  "pages": [
    {
      "id": "12345",
      "title": "Welcome",
      "slug": "welcome",
      "htmlPath": "../out/pages/welcome-12345.html",
      "labels": ["howto", "policy"]
    }
  ]
}
```

---

**You’re set.** Capture the canvas template, log in once, and run the importer. 🚀
