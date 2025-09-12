# Manual / Offline Install of `PnP.PowerShell` (No Repository Available, No Admin)

Yes — you can **download and side‑load** the module without using `Install-Module` and without admin rights. Below are safe, repeatable methods.

---

## Decision Tree (pick one)

- **Have access to any machine with internet?** → **Method 1 (Recommended): `Save-Module` on a helper PC**, then copy the folders to the target machine.
- **Only a web browser is available (PowerShell Gallery blocked in console)?** → **Method 2: Browser “Manual Download” (.nupkg)** for each required module and extract by hand.
- **You can run PowerShell 7 portable (ZIP, no admin)** and the gallery works there → **Method 3: Use pwsh portable to install to CurrentUser**, then copy modules if needed.

> These methods also work to side‑load **PowerShellGet** / **PackageManagement** if those are missing on the target box.

---

## Method 1 — Use `Save-Module` on a helper machine (Recommended)

**On a helper machine with internet (no admin required):**
```powershell
# Ensure TLS 1.2
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# Download PnP.PowerShell (and dependencies) to a folder
Save-Module -Name PnP.PowerShell -Path C:\Temp\PS -Force

# (Optional) Also download these, in case the target lacks them:
Save-Module -Name PowerShellGet -RequiredVersion 2.2.5.1 -Path C:\Temp\PS -Force
Save-Module -Name PackageManagement -RequiredVersion 1.4.8.1 -Path C:\Temp\PS -Force
```

This creates a structure like:
```
C:\Temp\PS\
  PnP.PowerShell\<version>\*
  PnP.Framework\<version>\*           # dependency, auto-saved
  PowerShellGet\2.2.5.1\*            # optional
  PackageManagement\1.4.8.1\*        # optional
```

**Copy** those folders to the target machine under your **user modules** path:

- Windows PowerShell 5.1:
  ```
  %USERPROFILE%\Documents\WindowsPowerShell\Modules\
  ```
- PowerShell 7+:
  ```
  %USERPROFILE%\Documents\PowerShell\Modules\
  ```

You should end up with:
```
...\Modules\
  PnP.PowerShell\<version>\*.psd1/.psm1
  PnP.Framework\<version>\*
  (optional) PowerShellGet\2.2.5.1\*
  (optional) PackageManagement\1.4.8.1\*
```

**On the target machine:**
```powershell
# Optional: allow running user-scoped scripts
Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned -Force

# (Sometimes needed if files came from the internet)
Unblock-File -Path "$env:USERPROFILE\Documents\WindowsPowerShell\Modules\PnP.PowerShell\*\*" -Recurse -ErrorAction SilentlyContinue

# Import the module
Import-Module PnP.PowerShell

# Verify
Get-Command -Module PnP.PowerShell Connect-PnPOnline
```

> If `Import-Module` complains about a missing dependency (e.g., `PnP.Framework`), make sure its folder was copied under the same `Modules\` directory.

---

## Method 2 — Browser “Manual Download” of `.nupkg` packages

If the PowerShell Gallery console cmdlets don’t work on **any** machine, you can still download via a browser:

1. Go to **https://www.powershellgallery.com** and search:
   - `PnP.PowerShell`
   - `PnP.Framework` (dependency)
   - (optional) `PowerShellGet` `2.2.5.1`
   - (optional) `PackageManagement` `1.4.8.1`
2. Open each module page → click **“Manual Download”**. This downloads a `.nupkg` file.
3. On your machine, **rename** `.nupkg` → `.zip`, then **extract**.
4. Create this folder structure and place the extracted contents inside **a versioned subfolder** named exactly as the module version shown on the page:
   - Windows PowerShell 5.1:
     ```
     %USERPROFILE%\Documents\WindowsPowerShell\Modules\PnP.PowerShell\<version>\
     %USERPROFILE%\Documents\WindowsPowerShell\Modules\PnP.Framework\<version>\
     ```
   - PowerShell 7+:
     ```
     %USERPROFILE%\Documents\PowerShell\Modules\PnP.PowerShell\<version>\
     %USERPROFILE%\Documents\PowerShell\Modules\PnP.Framework\<version>\
     ```
5. Ensure each module folder contains the `.psd1` (manifest) and any `.psm1`/DLLs from the zip.
6. (Optional, if downloaded) repeat for `PowerShellGet` and `PackageManagement`.

Then:
```powershell
Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned -Force
Unblock-File -Path "$env:USERPROFILE\Documents\WindowsPowerShell\Modules\PnP.PowerShell\*\*" -Recurse -ErrorAction SilentlyContinue
Import-Module PnP.PowerShell
Get-Command -Module PnP.PowerShell Connect-PnPOnline
```

> **Tip:** Using `Save-Module` (Method 1) automatically pulls all transitive dependencies. With manual `.nupkg` downloads you must collect **each dependency** yourself — at minimum **PnP.Framework** for PnP.PowerShell.

---

## Method 3 — PowerShell 7 Portable (ZIP, no admin)

1. Download the **PowerShell 7 portable ZIP** from Microsoft (from a machine with internet) and extract, e.g.:
   ```
   C:\Users\<you>\Tools\pwsh\
   ```
2. Run `pwsh.exe` from that folder.
3. If the Gallery is reachable from this `pwsh`, do:
   ```powershell
   Install-Module PnP.PowerShell -Scope CurrentUser -Force
   Import-Module PnP.PowerShell
   ```
4. If the target box is completely offline, use **Method 1** on any machine and copy the resulting `PnP.PowerShell`/`PnP.Framework` folders into the **PowerShell 7** user modules path:
   ```
   %USERPROFILE%\Documents\PowerShell\Modules\
   ```

---

## Verify & Use

```powershell
Import-Module PnP.PowerShell
Get-Command -Module PnP.PowerShell Connect-PnPOnline

# (Optional) Test auth
Connect-PnPOnline -Url https://yourtenant.sharepoint.com/sites/YourSite -DeviceLogin
Get-PnPWeb
```

If `Import-Module` still fails, share the **exact error** and the output of:
```powershell
$PSVersionTable
$env:PSModulePath -split ';'
Get-ChildItem "$env:USERPROFILE\Documents\WindowsPowerShell\Modules" -Directory
```

---

## Notes & Gotchas

- **Paths:** Use *Windows PowerShell* path for 5.1 and *PowerShell 7* path for pwsh.
- **Execution Policy:** Use `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned` if needed.
- **Unblock:** Downloaded files can be marked as from the internet; `Unblock-File -Recurse` often fixes loading issues.
- **Dependencies:** `PnP.PowerShell` depends on `PnP.Framework` (copied automatically by `Save-Module`; manual if using `.nupkg`).
- **Proxy:** If you can reach the gallery via proxy on a helper machine, set it with:
  ```powershell
  [System.Net.WebRequest]::DefaultWebProxy.Credentials = [System.Net.CredentialCache]::DefaultCredentials
  ```

---

## Next

Once the module loads, you can run the migration scripts from the runbook you already have:
- **Runbook:** `Confluence_to_SharePoint_Migration_Runbook.md`
- **PSGallery Fix:** `PSGallery_Fix_Guide.md`
- **Non-Admin Install:** `PnP_PowerShell_NonAdmin_Install_Guide.md`
