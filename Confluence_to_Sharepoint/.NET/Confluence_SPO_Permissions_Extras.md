# Extra Permissions Checklist — Confluence & SharePoint Online (beyond app consent)

This guide covers the **additional permissions** and settings commonly missed when migrating from **Confluence** to **SharePoint Online**, regardless of whether you use .NET, Python, or PowerShell.

---

## Confluence (Cloud or Server/Data Center)

### Minimum to export / pull data
- **Space Admin** on every space being migrated (needed for **Space → Export → XML**).
- Visibility to **all pages** in scope, including those with **page restrictions**; otherwise the export/API will omit them.
- Ability to **download attachments** (images, diagrams, files behind `/download/attachments/...`).  
- For API-driven extraction: a licensed user with **REST API access** (Cloud: user + **API token**).

### Nice to have / sometimes needed
- Temporary **Site/Confluence Admin** if you must:
  - Bulk remove or adjust **page restrictions** pre-export.
  - Run **whole-site exports** or audit global permissions.
- Access to storage used by **macro apps**:
  - **Gliffy / diagrams.net**: diagrams are usually stored as page attachments—ensure read access.
  - **Jira or other dynamic macros**: if you plan to snapshot macro output, ensure the account also has rights on those external systems (or accept flatten/remove).

---

## SharePoint Online (target site)

### Minimum to run the migrator end-to-end
- **Site Owner** on the target site (recommended). Covers:
  - Create & **publish** modern pages in **Site Pages**.
  - Upload files to the chosen attachments library (e.g., **Documents**).
  - Edit **page metadata** (labels/columns).
- If **content approval** is enabled on **Site Pages**: the account needs **Approve Items** (be an **Approver** or part of **Owners** with that permission).
- If the library has **Require Check Out** enabled: either disable during migration or ensure your tool **checks out/in** items.

### Labels / managed metadata
- **Enterprise Keywords** route (simple): enable **Enterprise Metadata and Keywords** on **Site Pages**; no Term Store admin needed.
- **Custom term sets** route: a **Term Store Administrator** or **Group Manager** must create/manage term sets and grant the migrator account permission to tag with those terms.

### Changing site schema (if applicable)
- Creating **site columns**, **content types**, or changing **library settings** requires **Site Owner** (or permissions that include **Manage Lists/Design**). Tenant admin is not required unless doing **tenant-level** content types/hub publishing.

### App-only variants (if you avoid delegated user flow)
- **Graph `Sites.Selected`** (least privilege): tenant admin grants the permission and then **assigns per-site `write`** to the app.
- **SharePoint app-only (broad)**: tenant admin consent to **AllSites.FullControl** (often harder to approve).  
  *Note:* Even with app-only, page publishing can be affected by library rules (approval/checkout).

---

## Good practice & risk reduction
- Use a **service account** on both systems:
  - Confluence: add as **Space Admin** and to any **page restrictions** for spaces in scope.
  - SharePoint: add to the site’s **Owners** group.
- Run a **pilot export** and confirm the XML includes **restricted pages and attachments** before transforming.
- Check **Site Pages** settings (approval/checkout), ensure sufficient **site storage quota**.

---

## Pre-flight checklist

**Confluence**
- [ ] Exporting user is **Space Admin** for each space in scope.
- [ ] Exporting user can view **all restricted pages** required for migration.
- [ ] Attachments are accessible and downloadable.
- [ ] (If needed) Temporary **Site/Confluence Admin** arranged for bulk permission fixes.
- [ ] Macro storage (e.g., Gliffy/diagrams.net) readable by the export user.

**SharePoint Online**
- [ ] Migrator identity is **Site Owner** on the target site.
- [ ] **Site Pages** exists; **Documents** (or chosen library) ready for attachments.
- [ ] **Content approval** setting reviewed; migrator has **Approver** if required.
- [ ] **Require Check Out** disabled during migration, or tooling handles checkout/in.
- [ ] **Enterprise Keywords** enabled on Site Pages **or** a fallback text column (e.g., `ConfLabels`) created.
- [ ] (If custom taxonomy) Term Store role (Admin/Group Manager) assigned; term sets ready.
- [ ] Storage quota sufficient for attachments.
- [ ] If using **app-only**: tenant admin consent granted and **Sites.Selected** write assigned per site (or approved broad app-only).

---

**Tip:** Lock down changes after migration (re-enable approvals/checkout if your governance requires it), and keep a **page ID → new URL** mapping for comms and redirects.
