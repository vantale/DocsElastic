# Confluence → SharePoint Online (Modern Pages) Migration Runbook (No Third-Party Tools)

This runbook moves what is **realistically achievable without paid connectors**:

- ✅ **Pages** (rich text / headings / lists / tables)
- ✅ **Images & file attachments** (uploaded to a SharePoint library and re-linked)
- ✅ **Links between pages** (rewritten based on pageId mapping)
- ✅ **Labels** → SharePoint metadata (Enterprise Keywords or a custom column)
- ⚠️ **Comments** → flattened into a static “Imported comments” section at the bottom of each page
- ⚠️ **Simple macros** (TOC, Expand, Code) → flattened/static; no dynamic behavior

> Not included: editable Gliffy diagrams (exported as images instead), dynamic macros (Jira, Roadmaps, etc.), Confluence version history, or per‑page permissions parity.

---

## 0) Prerequisites

- **Confluence Space XML export** (include attachments + comments):  
  *Space Tools → Content Tools → Export → XML (Full)*
- **PowerShell 7+** and **PnP.PowerShell**:
  ```powershell
  Install-Module PnP.PowerShell -Scope CurrentUser
  ```
- Target **SharePoint Online Communication Site** (modern). Ensure the **Site Pages** library exists.
- (Recommended) Enable **Enterprise Keywords** on Site Pages (labels mapping):  
  Site Pages → Library Settings → *Enterprise Metadata and Keywords Settings* → ✔ Add Enterprise Keywords column.

**Local workspace layout** (you can change names, but keep relative structure consistent with the scripts):
```
confluence-to-spo/
  export/                # put Confluence XML export here (entities.xml, attachments/)
  out/
    pages/               # generated HTML per page (from Python transformer)
    attachments/         # optional staging; not strictly required
    manifest.json        # index describing pages/labels/links/comments
  scripts/
    confluence_transform.py
    migrate.ps1
```

---

## 1) Transformer (Confluence XML → HTML + manifest.json)

**What it does**
- Parses `entities.xml` from the Confluence export.
- Emits **one cleaned HTML file per page** into `out/pages`.
- **Flattens comments** into an “Imported comments” section appended to each page HTML.
- Extracts **labels** per page.
- Captures references to **attachments** and **links** for later rewriting.
- Writes `out/manifest.json` consumed by the PowerShell orchestrator.

> Keep this as a practical starter. Confluence XMLs vary; extend the selectors and macro handling for your content if needed.

**File:** `scripts/confluence_transform.py`
```python
import os, re, json, html
import xml.etree.ElementTree as ET
from pathlib import Path

EXPORT_DIR = Path(__file__).resolve().parents[1] / "export"
OUT_DIR = Path(__file__).resolve().parents[1] / "out"
PAGES_DIR = OUT_DIR / "pages"
ATT_DIR = EXPORT_DIR / "attachments"   # from the Confluence export zip
PAGES_DIR.mkdir(parents=True, exist_ok=True)
(OUT_DIR / "attachments").mkdir(parents=True, exist_ok=True)

ENTITIES = EXPORT_DIR / "entities.xml"
tree = ET.parse(ENTITIES)
root = tree.getroot()

NS = {"c": "http://www.atlassian.com/xml/ns/confluence/1"}

def text_or_empty(x): 
    return x.text if x is not None and x.text else ""

# Build page index (id → data)
pages = {}
for page in root.findall(".//c:object[@class='Page']", NS):
    pid_el = page.find("./c:id", NS)
    if pid_el is None or not pid_el.text:
        continue
    pid = pid_el.text.strip()

    title = text_or_empty(page.find("./c:property[@name='title']/c:value", NS)).strip()
    space = text_or_empty(page.find("./c:property[@name='space']/c:id", NS))
    parent = text_or_empty(page.find("./c:property[@name='parent']/c:id", NS))

    # Confluence “storage” (body) often lives here
    body_el = page.find("./c:property[@name='body']/c:property[@name='body']/c:value", NS)
    body = text_or_empty(body_el)
    html_body = body

    # --- Minimal macro flattening (extend as needed) ---
    # 1) Expand macro → <details>
    html_body = re.sub(
        r'<ac:structured-macro[^>]*ac:name="expand"[^>]*>(.*?)</ac:structured-macro>',
        r'<details class="conf-expand">\1</details>',
        html_body,
        flags=re.DOTALL
    )
    # 2) TOC macro → placeholder
    html_body = re.sub(
        r'<ac:structured-macro[^>]*ac:name="toc"[^>]*/?>',
        r'<div class="conf-toc">[Table of contents removed]</div>',
        html_body,
        flags=re.DOTALL
    )
    # 3) Code macro → <pre>
    html_body = re.sub(
        r'<ac:structured-macro[^>]*ac:name="code"[^>]*>.*?<ac:plain-text-body><!\[CDATA\[(.*?)\]\]></ac:plain-text-body>.*?</ac:structured-macro>',
        lambda m: f"<pre>{html.escape(m.group(1))}</pre>",
        html_body,
        flags=re.DOTALL
    )

    # Extract labels
    labels = []
    for lab in root.findall(f".//c:object[@class='Label'][c:property[@name='owner']/c:id='{pid}']", NS):
        lname = text_or_empty(lab.find("./c:property[@name='name']/c:value", NS)).strip()
        if lname:
            labels.append(lname)

    # Extract comments (flatten)
    comments_html = []
    for com in root.findall(f".//c:object[@class='Comment'][c:property[@name='content']/c:id='{pid}']", NS):
        author = text_or_empty(com.find("./c:property[@name='creatorName']/c:value", NS)).strip()
        cbody = text_or_empty(com.find("./c:property[@name='body']/c:property[@name='body']/c:value", NS))
        created = text_or_empty(com.find("./c:property[@name='creationDate']/c:date", NS))
        comments_html.append(
            f"<div class='conf-comment'><div><strong>{html.escape(author or 'unknown')}</strong> — {html.escape(created)}</div><div>{cbody}</div></div>"
        )

    if comments_html:
        html_body += "<hr><h2>Imported comments</h2>" + "\n".join(comments_html)

    # Collect original links and attachment-like URLs for later rewrite
    links = re.findall(r'href=\"([^\"]+)\"', html_body) + re.findall(r'src=\"([^\"]+)\"', html_body)
    attachments = [u for u in links if "/download/attachments/" in u]

    # Save page HTML (slug-based file name for readability)
    slug = re.sub(r"[^a-zA-Z0-9\-]+", "-", title).strip("-") or pid
    file_path = PAGES_DIR / f"{slug}-{pid}.html"
    with open(file_path, "w", encoding="utf-8") as f:
        f.write(html_body)

    pages[pid] = {
        "id": pid,
        "title": title or f"Untitled-{pid}",
        "slug": slug,
        "spaceId": space,
        "parentId": parent or None,
        "htmlPath": str(file_path),
        "labels": sorted(set(labels)),
        "attachments": attachments,   # URLs to rewrite later
        "originalLinks": list(sorted(set(links)))
    }

# Manifest (for PowerShell)
manifest = { "pages": list(pages.values()) }

with open(OUT_DIR / "manifest.json", "w", encoding="utf-8") as f:
    json.dump(manifest, f, ensure_ascii=False, indent=2)

print(f"OK: {len(pages)} pages → out/manifest.json")
```

**Run the transformer:**
```bash
cd scripts
python confluence_transform.py
```

---

## 2) Orchestrator (PnP PowerShell → create pages, upload files, rewrite links, set labels)

**What it does**
- Connects to SharePoint Online.
- Ensures an **attachments** folder (e.g., `Documents/ConfluenceAttachments`).
- **Creates all modern pages empty first** to obtain URLs.
- Builds a **mapping (ConfluenceId → SPO page URL)**.
- **Uploads attachments** referenced by pages (lazy, per reference).
- **Injects HTML** into each modern page.
- **Rewrites links**: Confluence `pageId` links → SPO URLs; attachment links → uploaded file URLs.
- **Stamps labels** onto Site Pages (Enterprise Keywords or your custom column).

> Start simple with a single Text web part per page. You can later move to Canvas JSON for complex layouts.

**File:** `scripts/migrate.ps1`
```powershell
param(
  [Parameter(Mandatory=$true)] [string] $SiteUrl,
  [Parameter(Mandatory=$true)] [string] $ManifestPath = "../out/manifest.json",
  [string] $AttachmentsLibrary = "Documents",             # target library for attachments
  [string] $AttachmentsFolder  = "ConfluenceAttachments", # folder path inside library
  [switch] $WhatIf
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Import-Module PnP.PowerShell

# 1) Connect
Write-Host "Connecting to $SiteUrl ..."
Connect-PnPOnline -Url $SiteUrl -Interactive

# 2) Read manifest
$manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json
$pages = @{}; $manifest.pages | ForEach-Object { $pages[$_.id] = $_ }

# 3) Ensure attachments folder
$attLib = Get-PnPList -Identity $AttachmentsLibrary
if (-not $attLib) { throw "Library '$AttachmentsLibrary' not found." }

$attFolderServerRelUrl = "$($attLib.RootFolder.ServerRelativeUrl.TrimEnd('/'))/$AttachmentsFolder"
try {
  Get-PnPFolder -Url $attFolderServerRelUrl -ErrorAction Stop | Out-Null
} catch {
  Write-Host "Creating folder: $attFolderServerRelUrl"
  if (-not $WhatIf) { Add-PnPFolder -Name $AttachmentsFolder -Folder $attLib.RootFolder.ServerRelativeUrl | Out-Null }
}

# 4) First pass: create all pages (empty) to get URLs
$pageMap = @{} # ConfluenceId → SPO page server-relative URL
$sitePages = Get-PnPList -Identity "Site Pages"

foreach ($p in $pages.Values) {
  $name = "$($p.slug).aspx"
  $existing = Get-PnPListItem -List $sitePages -PageSize 2000 | Where-Object { $_["FileLeafRef"] -eq $name }
  if (-not $existing) {
    Write-Host "Creating modern page: $name"
    if (-not $WhatIf) { Add-PnPPage -Name $p.slug -LayoutType Article | Out-Null }
  } else {
    Write-Host "Page exists: $name"
  }
  $item = Get-PnPListItem -List $sitePages | Where-Object { $_["FileLeafRef"] -eq $name }
  $fileRef = $item["FileRef"]
  $pageMap[$p.id] = $fileRef
}

# Helper: server-relative → absolute URL
function ToAbsoluteUrl([string]$serverRel) {
  $web = Get-PnPWeb
  $uri = [Uri]$web.Url
  return "$($uri.Scheme)://$($uri.Host)$serverRel"
}

# 5) Upload attachments per reference; build URL rewrite map
$attachmentUrlMap = @{} # original confluence url → absolute SPO url
$attachmentsRoot = $null
try { $attachmentsRoot = (Resolve-Path ../export/attachments -ErrorAction Stop).Path } catch {}

foreach ($p in $pages.Values) {
  $html = Get-Content $p.htmlPath -Raw
  $matches = Select-String -InputObject $html -Pattern '["''](\/download\/attachments\/[^"''\s]+)["'']' -AllMatches
  if (-not $matches) { continue }

  foreach ($m in $matches.Matches) {
    $url = $m.Groups[1].Value

    # Derive a filename from the URL
    $dummyUri = [Uri]("http://dummy" + $url)
    $fileName = $dummyUri.Segments[-1]
    $fileName = [Uri]::UnescapeDataString($fileName)

    # Locate the file locally within export/attachments
    $localPath = $null
    if ($attachmentsRoot) {
      $candidate = Join-Path $attachmentsRoot $fileName
      if (Test-Path $candidate) {
        $localPath = $candidate
      } else {
        $found = Get-ChildItem $attachmentsRoot -Recurse -File -Filter $fileName -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) { $localPath = $found.FullName }
      }
    }

    if ($localPath -and (Test-Path $localPath)) {
      $targetFolderUrl = "$attFolderServerRelUrl/$($p.slug)"
      try { Get-PnPFolder -Url $targetFolderUrl -ErrorAction Stop | Out-Null } catch {
        if (-not $WhatIf) { Add-PnPFolder -Name $p.slug -Folder $attFolderServerRelUrl | Out-Null }
      }
      $targetUrl = "$targetFolderUrl/$fileName"
      $targetAbs = ToAbsoluteUrl $targetUrl

      # Upload if not exists
      $exists = $false
      try { Get-PnPFile -Url $targetUrl -ErrorAction Stop | Out-Null; $exists = $true } catch {}
      if (-not $exists) {
        Write-Host "Uploading attachment $fileName for page $($p.title)"
        if (-not $WhatIf) { Add-PnPFile -Path $localPath -Folder $targetFolderUrl | Out-Null }
      }
      $attachmentUrlMap[$url] = $targetAbs
    } else {
      Write-Warning "Attachment file not found for URL: $url (expected name: $fileName)"
    }
  }
}

# 6) Inject content, rewrite links, stamp labels
foreach ($p in $pages.Values) {
  $name = "$($p.slug).aspx"
  $pageUrl = $pageMap[$p.id]
  $html = Get-Content $p.htmlPath -Raw

  # Rewrite Confluence page links → SPO modern pages (detect ?pageId=12345)
  $html = [Regex]::Replace($html, 'href="[^"]*pageId=(\d+)[^"]*"', {
      param($m)
      $cid = $m.Groups[1].Value
      if ($pageMap.ContainsKey($cid)) { 'href="' + (ToAbsoluteUrl $pageMap[$cid]) + '"' } else { $m.Value }
  })

  # Rewrite attachment URLs
  foreach ($kv in $attachmentUrlMap.GetEnumerator()) {
    $old = [Regex]::Escape($kv.Key)
    $new = $kv.Value
    $html = [Regex]::Replace($html, "(href|src)=""$old""", "`$1=""$new""")
  }

  Write-Host "Updating page content: $name"
  if (-not $WhatIf) {
    # (Re)publish the page with a single Text part containing HTML
    # Remove default webpart if present (ignore errors), then add the text part
    Remove-PnPPageWebPart -Page $p.slug -Identity 0 -ErrorAction SilentlyContinue | Out-Null
    Add-PnPPageTextPart -Page $p.slug -Text $html | Out-Null
    Publish-PnPPage -Identity $p.slug | Out-Null
  }

  # Labels → Enterprise Keywords (internal name typically 'TaxKeyword' in libraries)
  if ($p.labels.Count -gt 0) {
    try {
      $item = Get-PnPListItem -List "Site Pages" | Where-Object { $_["FileLeafRef"] -eq $name }
      if ($item) {
        Write-Host "Stamping labels on $name: $($p.labels -join ', ')"
        if (-not $WhatIf) {
          # If Enterprise Keywords isn't enabled, create a custom column (e.g., 'ConfLabels') and set that instead.
          Set-PnPListItem -List "Site Pages" -Identity $item.Id -Values @{ "TaxKeyword" = ($p.labels -join ";") } | Out-Null
        }
      }
    } catch {
      Write-Warning "Could not set labels for $name. Ensure Enterprise Keywords (field internal name 'TaxKeyword') exists or adjust the field name in the script."
    }
  }
}

Write-Host "Done."
```

**Run the orchestrator:**
```powershell
cd scripts
.\migrate.ps1 -SiteUrl "https://YOURTENANT.sharepoint.com/sites/YourSite" -ManifestPath "../out/manifest.json"
```

---

## 3) Validation checklist

- **Formatting**: 10 representative pages (tables, lists, headings) render properly.
- **Images & files**: inline images download from the new library; links open.
- **Internal links**: several intra-wiki links jump to the correct SPO pages.
- **Labels**: page properties show Enterprise Keywords (or your custom field).
- **Comments**: “Imported comments” section is visible and readable.

---

## 4) Known limitations (by design)

- **Threaded comments** → flattened into static HTML (no replies/likes/mentions).
- **Gliffy diagrams** → require export to PNG/SVG (editability lost). Convert to Visio/Lucidchart manually if needed.
- **Dynamic macros** (Jira, Roadmaps, etc.) → flattened or removed; rebuild with SharePoint web parts or Lists.
- **Version history** and **per-page permissions** are not ported.

---

## 5) Customization ideas

- **Macro handlers**: Extend the Python regex section for macros you encounter frequently.
- **Canvas JSON**: Replace the simple “Text part” approach with full *CanvasContent1* JSON if you want sections/columns/hero web parts.
- **Pretty link mapping**: If your exports use `/display/SPACE/Page+Title` links, add a pass to map titles → Confluence IDs in Python, then use `$pageMap` in PowerShell to rewrite based on IDs.
- **Labels taxonomy**: Precreate a Managed Metadata term set and map labels to canonical terms (synonyms, casing, etc.).
- **Attachment strategy**: Upload all attachments upfront (not lazily) and build a full `oldUrl → newUrl` map before page writes.

---

### Quick start recap

1) Put your **Confluence XML export** under `export/` (including `attachments/`).  
2) Run the **transformer** to create `out/pages/*.html` and `out/manifest.json`.  
3) Run **migrate.ps1** with your **SharePoint site URL**.  
4) Review sample pages, fix macro/format edge cases, then roll out to more spaces.

Good luck! 🚀
