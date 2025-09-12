# PnP.PowerShell on Windows PowerShell 5.1 — **No Admin** Install Guide

This guide shows three **non‑admin** ways to get `PnP.PowerShell` working on **Windows PowerShell 5.1** (your version reported earlier). Pick the option that matches your environment.

---

## Option A — Install to **CurrentUser** (no admin)

Run these in a **normal** PowerShell 5.1 window:

```powershell
# Use TLS 1.2 so PowerShell Gallery (PSGallery) works
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# (Optional) allow scripts in your user scope
Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned -Force

# Register PSGallery (user scope) & trust it
Register-PSRepository -Default -ErrorAction SilentlyContinue
Set-PSRepository -Name PSGallery -InstallationPolicy Trusted

# Ensure NuGet provider (will install under your user profile if needed)
Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force

# Update the module managers (user scope)
Install-Module PackageManagement -Scope CurrentUser -Force -AllowClobber
Install-Module PowerShellGet -Scope CurrentUser -RequiredVersion 2.2.5.1 -Force -AllowClobber

# RESTART the PowerShell console, then:
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Install-Module -Name PnP.PowerShell -Scope CurrentUser -Force

# Verify
Import-Module PnP.PowerShell
Get-Command -Module PnP.PowerShell Connect-PnPOnline
```

**Behind a corporate proxy?** Configure before installing:

```powershell
[System.Net.WebRequest]::DefaultWebProxy.Credentials = [System.Net.CredentialCache]::DefaultCredentials
Install-Module PnP.PowerShell -Scope CurrentUser -Proxy http://your.proxy:8080 -ProxyCredential (Get-Credential) -Force
```

---

## Option B — **Side‑load** the module (zero install)

Use this if PSGallery is blocked or you’re offline on the target box.

1) On any PC with internet (no admin needed), run:
```powershell
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Save-Module -Name PnP.PowerShell -Path C:\Temp\PnP
```
This downloads **PnP.PowerShell and its dependencies** into `C:\Temp\PnP\PnP.PowerShell\<version>\`.

2) Copy the whole `PnP.PowerShell` folder to the target machine into:
```
%USERPROFILE%\Documents\WindowsPowerShell\Modules\PnP.PowerShell\
```
(For PowerShell 7, use: `%USERPROFILE%\Documents\PowerShell\Modules\PnP.PowerShell\`.)

3) Then import:
```powershell
Import-Module PnP.PowerShell
```

> If `Import-Module` complains about missing dependencies (e.g., `PnP.Framework`), also copy any sibling folders from `C:\Temp\PnP\` to your `...\Modules\` directory.

---

## Option C — Use **PowerShell 7 portable** (no admin)

PowerShell 7 installs side‑by‑side and avoids many 5.1 quirks.

1) Download the **portable ZIP** of PowerShell 7 from Microsoft and extract to e.g.:
```
C:\Users\<you>\Tools\pwsh\
```
2) Launch `pwsh.exe` from that folder.
3) In `pwsh`:
```powershell
Install-Module PnP.PowerShell -Scope CurrentUser -Force
Import-Module PnP.PowerShell
```

---

## Next step

Once `PnP.PowerShell` imports cleanly, proceed with your migration runbook:

- **Runbook**: [Confluence_to_SharePoint_Migration_Runbook.md](sandbox:/mnt/data/Confluence_to_SharePoint_Migration_Runbook.md)

If anything still blocks installation, share the outputs of:
```powershell
$PSVersionTable
Get-PSRepository
Find-Module PnP.PowerShell -AllVersions
```
and I’ll tailor the fix (proxy/offline/dependency).
