# Fix: "No repository with the name 'PSGallery' was found" (No‑Admin, Windows PowerShell 5.1)

This guide helps you register **PSGallery** and install `PnP.PowerShell` **without admin rights** on Windows PowerShell 5.1.

---

## A) You **have** `Register-PSRepository`

```powershell
# 1) Use TLS 1.2 so PowerShell Gallery works
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# 2) (Re)register PSGallery (v2 feed)
$src = 'https://www.powershellgallery.com/api/v2'
Unregister-PSRepository -Name PSGallery -ErrorAction SilentlyContinue
Register-PSRepository -Name PSGallery -SourceLocation $src -ScriptSourceLocation $src -InstallationPolicy Trusted

# 3) Confirm it exists
Get-PSRepository

# 4) Install to CurrentUser (no admin)
Install-Module PnP.PowerShell -Scope CurrentUser -Force
```

> Note: The name is **PSGallery** (case-insensitive), not "PsGallery".

---

## B) You **don’t have** `Register-PSRepository` (PowerShellGet missing/old)

Bootstrap **PowerShellGet** and **PackageManagement** to **CurrentUser** from a machine with internet (no admin).

**On a helper machine (with internet):**
```powershell
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Save-Module PowerShellGet -RequiredVersion 2.2.5.1 -Path C:\Temp\PS
Save-Module PackageManagement -RequiredVersion 1.4.8.1 -Path C:\Temp\PS
```

Copy these folders to your target machine under:
```
%USERPROFILE%\Documents\WindowsPowerShell\Modules\
```

**On the target machine:**
```powershell
Import-Module PackageManagement -Force
Import-Module PowerShellGet -Force

# Now register PSGallery and install PnP.PowerShell
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$src = 'https://www.powershellgallery.com/api/v2'
Register-PSRepository -Name PSGallery -SourceLocation $src -ScriptSourceLocation $src -InstallationPolicy Trusted
Install-Module PnP.PowerShell -Scope CurrentUser -Force
```

---

## C) Behind a corporate proxy?

```powershell
# Use default Windows credentials for the proxy
[System.Net.WebRequest]::DefaultWebProxy.Credentials = [System.Net.CredentialCache]::DefaultCredentials

$src = 'https://www.powershellgallery.com/api/v2'
Register-PSRepository -Name PSGallery `
  -SourceLocation $src -ScriptSourceLocation $src -InstallationPolicy Trusted `
  -Proxy 'http://your.proxy:8080' -ProxyCredential (Get-Credential)

Install-Module PnP.PowerShell -Scope CurrentUser `
  -Proxy 'http://your.proxy:8080' -ProxyCredential (Get-Credential) -Force
```

---

## Diagnostics (paste outputs if it still fails)

```powershell
Get-Command Register-PSRepository
Get-Module PowerShellGet,PackageManagement -ListAvailable
Get-PSRepository
Find-Module PnP.PowerShell -AllVersions
```

If `Register-PSRepository` is missing and you cannot use a helper machine, we can side‑load modules directly (zip‑copy) similar to the **Side‑load** method in the non‑admin guide.

---

## Next step

Once PSGallery is registered and `PnP.PowerShell` is installed, proceed with your migration:

- **Runbook**: Confluence_to_SharePoint_Migration_Runbook.md  
- **Non‑Admin Install Guide**: PnP_PowerShell_NonAdmin_Install_Guide.md
