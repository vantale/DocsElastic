# SharePoint/Graph Permissions — Do They Change for .NET vs Python vs PowerShell?

**Short answer:** **No.** The required permissions are essentially the same regardless of whether you use **.NET**, **Python**, or **PowerShell**. What changes is *how you obtain the token* (SDK vs. script), not *what SharePoint/Graph authorize*. The API gates are identical.

---

## What Always Stays the Same

- **Site-level rights:** Your *user* (for delegated auth) still needs **Site Owner** (or equivalent) on the target site to create/publish modern pages and upload files.
- **Library rules:** **Site Pages** versioning/approval settings apply regardless of language.
- **Tenant consent:** If you use wide-scope delegated or any app-only permissions, a **tenant admin** must consent — tool/language doesn’t change that.

---

## Auth/Permission Options (apply to .NET, Python, PowerShell)

| Option | Who runs | Typical tenant consent needed | When to choose |
|---|---|---|---|
| **Delegated (recommended for one-off)** | Your user identity | **SharePoint (Delegated):** `AllSites.FullControl`  <br> **Graph (Delegated):** `Sites.ReadWrite.All` *(optional if you avoid Graph)* | Simple to operate; honors your site permissions; good for pilots/one-time migrations. |
| **Application (least privilege via `Sites.Selected`)** | App identity (no user context) | **Graph (Application):** `Sites.Selected` + **per-site assignment** (admin grants and assigns **write** to each site) | When security wants app-only and scoped to specific sites. |
| **Application (broad)** | App identity | **SharePoint (Application):** `AllSites.FullControl` | Easiest app-only, but broadest access; often harder to get approved. |
| **Legacy ACS / Add-in model** | App identity | SharePoint Add-in permissions | Legacy & often disabled; generally avoid. |

---

## Notes by Tech Stack

- **.NET (PnP Core SDK)**  
  - By default may call **Graph** and **SharePoint**.  
  - You can set `GraphFirst = false` to minimize/avoid Graph. If you do so, you can skip Graph permissions and rely on SharePoint delegated/app-only.

- **PowerShell (PnP.PowerShell)**  
  - Same permission choices.  
  - If you use the **PnP Management Shell** app, an admin must consent once; or you can register your own app and consent the same way as with .NET.

- **Python (`office365-rest-python-client` / MSAL)**  
  - Same tokens, same consents.  
  - Delegated or app-only; Graph is optional if you stick to SharePoint REST/CSOM.

---

## Practical Recommendations

- **Fastest approval:** **Delegated** + make sure you’re **Site Owner**. If security balks at Graph, run **SharePoint-only** (no Graph scopes) and stick to SharePoint REST via your SDK.  
- **Tight security:** **Application with Graph `Sites.Selected`** and assign the app **write** on only the migration sites.  
- Regardless of route: ensure **Site Pages** settings (approval/checkout) won’t block publishing, and enable **Enterprise Keywords** if you plan to write labels to that field.
