# Monitor Folder Occupancy with Winlogbeat (Windows Server)

## What you’ll build
- A **PowerShell** script that measures a folder’s size, file/dir counts, and drive usage.
- It **writes a structured JSON message** into the **Windows Application** event log under a custom source `RepoMonitor`.
- **Winlogbeat** subscribes to that source, **parses the JSON**, ships it to Elasticsearch.
- **Kibana** shows charts + **alerts** when thresholds are crossed.

---

## 0) Prerequisites
- Elasticsearch + Kibana reachable from the server.
- Winlogbeat ZIP installed/extracted (no Elastic Agent needed).
- Admin PowerShell for creating an event source & installing Winlogbeat service.

> Tip: If your repository is huge (millions of files), start with a **10–15 min** schedule and tune later.

---

## 1) PowerShell emitter (writes JSON into Event Log)

Save as `C:\Scripts\Emit-RepoOccupancy.ps1`:

```powershell
param(
  [Parameter(Mandatory=$true)]
  [string[]]$Folders,

  [long]$QuotaBytes = 0,         
  [hashtable]$FolderQuotaBytes,  
  [int]$WarnPct = 80,
  [int]$CritPct = 90
)

$logName = "Application"
$source  = "RepoMonitor"
if (-not [System.Diagnostics.EventLog]::SourceExists($source)) {
  New-EventLog -LogName $logName -Source $source
}

function To-GB($bytes) { if ($bytes -ge 0) { [math]::Round($bytes / 1GB, 3) } else { $null } }

foreach ($folder in $Folders) {
  try {
    $driveLetter = (Get-Item -LiteralPath $folder).PSDrive.Name + ":"
  } catch {
    $driveLetter = $null
  }

  $items = @()
  try {
    $items = Get-ChildItem -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue
  } catch {}

  $files = $items | Where-Object { -not $_.PSIsContainer }
  $dirs  = $items | Where-Object { $_.PSIsContainer }

  $sizeBytes = ($files | Measure-Object Length -Sum).Sum
  if (-not $sizeBytes) { $sizeBytes = 0 }

  $fileCount = $files.Count
  $dirCount  = $dirs.Count

  $oldest = if ($files) { ($files | Sort-Object LastWriteTimeUtc | Select-Object -First 1).LastWriteTimeUtc } else { $null }
  $newest = if ($files) { ($files | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc } else { $null }

  $driveTotal = $null; $driveFree = $null; $driveUsed = $null; $driveUsedPct = $null
  if ($driveLetter) {
    $ld = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$driveLetter'"
    if ($ld) {
      $driveTotal = [long]$ld.Size
      $driveFree  = [long]$ld.FreeSpace
      $driveUsed  = $driveTotal - $driveFree
      if ($driveTotal -gt 0) { $driveUsedPct = [math]::Round(($driveUsed / $driveTotal) * 100, 2) }
    }
  }

  $q = if ($FolderQuotaBytes.ContainsKey($folder)) { [long]$FolderQuotaBytes[$folder] } elseif ($QuotaBytes -gt 0) { $QuotaBytes } else { 0 }
  $pctOfQuota = if ($q -gt 0) { [math]::Round(($sizeBytes / $q) * 100, 2) } else { $null }

  $evt = [ordered]@{
    "@timestamp"       = (Get-Date).ToUniversalTime().ToString("o")
    "event"            = @{ "dataset" = "repo.occupancy"; "kind" = "metric" }
    "host"             = $env:COMPUTERNAME
    "service"          = @{ "name" = "repo-occupancy" }
    "repo"             = @{
      "folder"         = $folder
      "size_bytes"     = $sizeBytes
      "size_gb"        = To-GB $sizeBytes
      "file_count"     = $fileCount
      "dir_count"      = $dirCount
      "oldest_file_ts" = $oldest
      "newest_file_ts" = $newest
      "quota_bytes"    = if ($q -gt 0) { $q } else { $null }
      "quota_gb"       = if ($q -gt 0) { To-GB $q } else { $null }
      "pct_of_quota"   = $pctOfQuota
    }
    "drive"            = @{
      "letter"         = $driveLetter
      "total_bytes"    = $driveTotal
      "free_bytes"     = $driveFree
      "used_bytes"     = $driveUsed
      "used_pct"       = $driveUsedPct
      "total_gb"       = To-GB $driveTotal
      "free_gb"        = To-GB $driveFree
      "used_gb"        = To-GB $driveUsed
    }
  }

  $json = ($evt | ConvertTo-Json -Depth 6 -Compress)

  $eid = 7001
  if ($pctOfQuota -ne $null) {
    if ($pctOfQuota -ge $CritPct) { $eid = 7003 }
    elseif ($pctOfQuota -ge $WarnPct) { $eid = 7002 }
  }

  Write-EventLog -LogName $logName -Source $source -EventId $eid -EntryType Information -Message $json
}
```

---

## 2) Schedule it (Task Scheduler)

Run every **5 minutes**:

```bat
schtasks /Create /TN "RepoOccupancy" ^
 /TR "powershell.exe -ExecutionPolicy Bypass -File C:\Scripts\Emit-RepoOccupancy.ps1 -Folders C:\Repository -QuotaBytes 536870912000 -WarnPct 80 -CritPct 90" ^
 /SC MINUTE /MO 5
```

---

## 3) Configure Winlogbeat

`C:\Program Files\Winlogbeat\winlogbeat.yml`:

```yaml
winlogbeat.event_logs:
  - name: Application
    providers:
      - provider: RepoMonitor
        include_messages: true
    ignore_older: 72h

processors:
  - decode_json_fields:
      fields: ["message"]
      target: "repo"
      process_array: false
      max_depth: 3
      overwrite_keys: false
      add_error_key: true

  - rename:
      fields:
        - from: "repo.repo"
          to: "repo"
        - from: "repo.drive"
          to: "drive"
        - from: "repo.event"
          to: "event_custom"
      ignore_missing: true
      fail_on_error: false

  - timestamp:
      field: "repo.@timestamp"
      layouts: ["2006-01-02T15:04:05.999999999Z07:00"]
      timezone: "UTC"
      ignore_missing: true

  - drop_event:
      when:
        not:
          equals:
            winlog.channel: "Application"
  - drop_event:
      when:
        not:
          equals:
            winlog.provider_name: "RepoMonitor"

fields_under_root: true
fields:
  data_stream.dataset: "repo.occupancy"
  data_stream.namespace: "default"
  data_stream.type: "logs"

setup.ilm.enabled: true
setup.template.enabled: true
setup.template.type: index

output.elasticsearch:
  hosts: ["https://YOUR-ES:9200"]
  username: "winlogbeat"
  password: "********"
  ssl.verification_mode: certificate
```

---

## 4) Install & Start Winlogbeat

```powershell
cd 'C:\Program Files\Winlogbeat'
.\install-service-winlogbeat.ps1
Start-Service winlogbeat
```

---

## 5) Kibana Visualizations

- **Time series**: `max(repo.size_gb)` per `repo.folder`
- **Gauge**: `max(repo.pct_of_quota)` per folder
- **Freshness**: `repo.newest_file_ts`

---

## 6) Alerts

Threshold rule: `repo.pct_of_quota >= 80` → Warn, `>= 90` → Critical  
Alternative: alert on `winlog.event_id:7002` and `7003`.

---

## 7) Troubleshooting

- Check **Event Viewer → Application** for `Source: RepoMonitor`
- Check Winlogbeat logs in `C:\ProgramData\winlogbeat\Logs`
- If JSON not parsed: ensure `include_messages: true` and `decode_json_fields.fields: ["message"]`

---
