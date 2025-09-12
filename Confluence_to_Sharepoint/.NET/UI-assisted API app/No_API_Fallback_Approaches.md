# Migrating When API Access Isn’t Available — Practical Fallbacks

If tenant security won’t approve **Graph/SharePoint API consents** but users can still work in the SharePoint **web UI**, you can still migrate successfully. Here’s how.

---

## What “no API” usually means

- No admin consent for **Graph/SharePoint** delegated/app permissions.  
- Users **can** sign in and use SharePoint normally in a browser.

If the UI works, it’s already calling SharePoint REST under the hood. We can reuse that user session safely.

---

## Plan A — **UI‑assisted API** (recommended fallback, no tenant app consent)

Automate a real browser session (e.g., **Playwright for .NET**) to sign in as a user, then call SharePoint REST **from the authenticated page** (reusing the user’s cookie). You avoid the page designer clicks and still don’t need any app registration or tenant-wide scopes.

**How it works**

1. **Sign in once** with a persistent browser profile (MFA/SSO ok).  
2. Fetch a **request digest**: `POST /_api/contextinfo` (from that page).  
3. For each Confluence page (from your manifest):
   - **Create** a modern page: `POST /_api/sitepages/pages` (PageLayoutType = `Article`).  
   - **Upload attachments**:  
     `POST /_api/web/GetFolderByServerRelativeUrl('/Shared Documents/ConfluenceAttachments/{slug}')/Files/add(url='file.ext',overwrite=true)`  
   - **Set CanvasContent1** (add a Text web part with your rewritten HTML):  
     `POST /_api/sitepages/pages({id})/SavePageAsDraft`  
   - **Publish**: `POST /_api/sitepages/pages({id})/Publish`  
   - **Stamp labels** (Enterprise Keywords or a custom column) by updating the Site Pages list item field.

**Why it works**
- No new app or admin consent — you use the **same user session** as the browser UI.  
- More robust than clicking the editor; you call official REST endpoints.

**When it won’t work**
- If Conditional Access blocks automation/headless browsers.  
- If SharePoint REST were blocked for user sessions (the UI would also fail).

---

## Plan B — **Pure UI automation** (last resort)

Automate clicks/typing like a user (Playwright/Selenium/RPA). Flow:

1. **New → Site page**.  
2. Set **Title**.  
3. Add **Text** web part; paste sanitized HTML.  
4. Insert **images** via the dialog.  
5. Open **Page details**; set **labels/metadata**.  
6. **Publish**.

**Trade‑offs**
- **Fragile** (DOM/labels change; selectors break).  
- **Sanitization** reduces HTML fidelity more than Canvas JSON.  
- **Slow** for images (upload dialogs).

---

## Confluence side — avoid UI scraping

Even without API approval on SharePoint, **don’t scrape Confluence’s UI**. Use:

- **Space XML export** (includes pages, comments, labels, attachment refs), or  
- **Confluence REST** with **user API token** to pull `body.storage`, labels, comments, and attachments.

This produces clean **HTML + manifest** for your SharePoint importer (UI‑assisted API or pure UI).

---

## Choosing the right fallback

| Situation | Best approach |
|---|---|
| No app consent, browser sign‑in works | **UI‑assisted API** (Playwright + REST via user session) |
| Programmatic REST forbidden, UI allowed | **Pure UI automation** (be prepared for brittleness) |
| Both automation and REST blocked | Manual migration or revisit policy/approvals |

---

## Guardrails for either fallback

- Use a **persistent browser profile**; complete MFA once.  
- Add **retry/backoff** for 429/503 and **screenshot on error**.  
- **Throttle** (e.g., 5–10 pages/min) to avoid throttling.  
- Keep a **mapping log** (`confluenceId → new SPO URL`).  
- Pilot a small space before scaling.

---

## Bottom line

- If the UI works, you can proceed **without tenant app consent** using the **UI‑assisted API** pattern (most robust “no API approval” option).  
- Pure UI clicking is possible but fragile — use only as a **last resort**.  
- Extract from Confluence via **export/REST**, not UI scraping, for clean inputs.
