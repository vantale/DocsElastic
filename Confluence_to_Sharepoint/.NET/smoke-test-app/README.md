# UI-Assisted SharePoint Smoke Test

Verifies login & cookie capture using a system browser (Chrome/Edge) via CDP, then calls `/_api/web/title`.

## Configure
Edit `appsettings.json`:
```json
{
  "TargetSiteUrl": "https://yourtenant.sharepoint.com/sites/YourSite",
  "ApiCheckPath": "/_api/web/title",
  "RemoteDebuggingPort": 9222,
  "WaitTimeoutSeconds": 300,
  "CookiesOutputPath": "cookies.json"
}
```

## Launch browser with remote debugging
Edge:
```cmd
start msedge.exe --remote-debugging-port=9222 --profile-directory=Default
```
Chrome:
```cmd
start chrome.exe --remote-debugging-port=9222 --profile-directory=Default
```

## Run
```powershell
dotnet build
dotnet run -- --site=https://yourtenant.sharepoint.com/sites/YourSite
```

Watch the console, complete M365 login, wait for `FedAuth` + `rtFa`, then the tool will call `/_api/web/title` and print the result.
