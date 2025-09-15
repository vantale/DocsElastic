# Runbook — EdgeCookieSmokeTest (UI-Assisted SharePoint Cookie Smoke Test)

## 📌 Purpose
This runbook explains how to run the **EdgeCookieSmokeTest** application to verify:
1. Login to SharePoint using **system-installed Microsoft Edge**.  
2. Automatic capture of **FedAuth + rtFa** cookies via **Playwright CDP attach**.  
3. Successful call to the SharePoint REST API (`/_api/web/title`) using those cookies.

No additional browser installation is required. The app attaches to **your existing Edge** instance.

---

## 🛠️ Prerequisites
- **Windows** workstation with Microsoft Edge installed.  
- **.NET 8 SDK** installed.  
- Access to your target SharePoint site.  
- Ability to close and restart Edge if it is already running (required to enable remote debugging).

---

## 📂 Package Contents
- `EdgeCookieSmokeTest.sln` — solution file  
- `EdgeCookieSmokeTest/EdgeCookieSmokeTest.csproj` — project file  
- `EdgeCookieSmokeTest/Program.cs` — main app logic  
- `EdgeCookieSmokeTest/appsettings.json` — configuration file  
- `EdgeCookieSmokeTest/README.md` — short description  

---

## ⚙️ Configuration
Edit **`EdgeCookieSmokeTest/appsettings.json`**:

```json
{
  "TargetSiteUrl": "https://yourtenant.sharepoint.com/sites/YourSite",
  "ApiCheckPath": "/_api/web/title",
  "RemoteDebuggingPort": 9222,
  "WaitTimeoutSeconds": 300,
  "CookiesOutputPath": "cookies.json",
  "EdgeExecutable": null,
  "ProfileDirectory": "Default"
}
```

- `TargetSiteUrl`: URL of your SharePoint site.  
- `ApiCheckPath`: REST endpoint to test (default: `/_api/web/title`).  
- `RemoteDebuggingPort`: DevTools port for Edge (default: 9222).  
- `WaitTimeoutSeconds`: Maximum wait time for cookies (default: 300s).  
- `CookiesOutputPath`: File where cookies are saved.  
- `EdgeExecutable`: Leave `null` to auto-detect Edge, or set full path (e.g., `C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe`).  
- `ProfileDirectory`: Which Edge profile to use (`Default` recommended).  

---

## 🚀 Running the App

### 1. Build
```powershell
dotnet build .\EdgeCookieSmokeTest.sln
```

### 2. Run
```powershell
dotnet run --project .\EdgeCookieSmokeTest\EdgeCookieSmokeTest.csproj -- --site=https://yourtenant.sharepoint.com/sites/YourSite
```

### 3. Behavior
- If Edge is not already running with DevTools, the app **launches Edge** with:  
  ```
  msedge.exe --remote-debugging-port=9222 --profile-directory=Default
  ```
- Edge opens, and you log in to your SharePoint site as usual.  
- The app waits until **FedAuth** and **rtFa** cookies are present.  
- Cookies are written to `cookies.json`.  
- The app calls `/_api/web/title` using the cookies.  
- The response (site title) is printed in the console.  

---

## ✅ Expected Output
- Console message confirming cookies were captured:
  ```
  ✅ Login detected (FedAuth + rtFa).
  Saved cookies → cookies.json
  ```
- HTTP 200 response from SharePoint:
  ```
  HTTP 200 OK
  Response:
  {"Title":"Your Site Name"}
  ```

---

## ⚠️ Troubleshooting
- **Edge already running**: Close all Edge windows before starting the app.  
- **Timeout waiting for cookies**: Ensure you completed login (MFA, redirect, etc.).  
- **HTTP 401/403**: Check that `TargetSiteUrl` matches your site’s exact URL.  
- **Edge not found**: Set `EdgeExecutable` in `appsettings.json` to the full path.  
- **Proxy issues**: This app avoids Playwright’s browser downloads — it should work in proxy-restricted environments.  

---

## 🔐 Security Notes
- `cookies.json` contains **active authentication cookies**. Handle it securely:  
  - Do not commit it to version control.  
  - Delete it after testing.  
- This tool is for **smoke testing** only. For production integrations, prefer **OAuth2 / Microsoft Graph API**.
