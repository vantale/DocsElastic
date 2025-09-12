# Monitoring Windows Event Log (Errors & Warnings) in Kibana with Winlogbeat

This guide shows how to ship **Application/System errors & warnings** from a Windows Server into Elasticsearch and display them in a Kibana dashboard.  
We will use **Winlogbeat** (no Elastic Agent required).

---

## 1. Install Winlogbeat

1. Download the Winlogbeat ZIP matching your Elasticsearch/Kibana version.  
2. Unzip to `C:\Elastic\Winlogbeat`.  
3. Open an elevated PowerShell in that folder and run:
   ```powershell
   .\install-service-winlogbeat.ps1
   ```

---

## 2. Configure `winlogbeat.yml`

Minimal configuration to collect **Application** and **System** events, but ship only **Errors** and **Warnings**.

```yaml
winlogbeat.event_logs:
  - name: Application
    ignore_older: 72h
  - name: System
    ignore_older: 72h

processors:
  - drop_event:
      when:
        not:
          or:
            - equals:
                winlog.level: error
            - equals:
                winlog.level: warning
  - add_tags:
      tags: [windows, app-errwarn]

output.elasticsearch:
  hosts: ["https://<your-elasticsearch>:9200"]
  username: "<user>"
  password: "<pass>"
  ssl:
    verification_mode: full

setup.template.enabled: true
setup.ilm.enabled: true

setup.kibana:
  host: "https://<your-kibana>:5601"
  username: "<user>"
  password: "<pass>"
# setup.dashboards.enabled: true
```

Restart service after editing:

```powershell
Restart-Service winlogbeat
```

---

## 3. Validate Data Flow

In Kibana → **Discover**, select or create the data view `winlogbeat-*`.  
Check fields like:

- `winlog.channel: Application`
- `winlog.level: error|warning`
- `winlog.provider_name`
- `event.code` / `winlog.event_id`
- `message`

---

## 4. Useful KQL Filters

Errors and warnings from Application:

```
winlog.channel:"Application" and (winlog.level:"error" or winlog.level:"warning")
```

Errors and warnings from Application + System:

```
(winlog.channel:"Application" or winlog.channel:"System")
and (winlog.level:"error" or winlog.level:"warning")
```

Filter by provider:

```
winlog.provider_name:"MSSQLSERVER"
```

---

## 5. Build a Kibana Dashboard

Create a new **Dashboard** and add:

### a) Errors/Warnings over time
- X-axis: `@timestamp` (date histogram)  
- Y-axis: Count  
- Filter: `(Application or System) AND (error|warning)`

### b) Top 10 Providers
- Horizontal bar  
- Breakdown: `Top values of winlog.provider_name`  
- Filter: same as above  

### c) Top Event IDs
- Horizontal bar  
- Breakdown: `Top values of event.code`  
- Filter: same as above  

### d) Recent Events Table
- Columns: `@timestamp`, `winlog.level`, `winlog.provider_name`, `event.code`, `message`  
- Sort: `@timestamp` descending  

Save as **Windows – App Errors & Warnings**.

---

## 6. (Optional) Registered Providers Check

List registered **Application** event sources (from registry):

```powershell
Get-ChildItem "HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application" |
  Select-Object -ExpandProperty PSChildName | Sort-Object
```

These values correspond to `winlog.provider_name`.  
You can filter in Kibana, e.g.:

```
winlog.provider_name:("MSSQLSERVER" or "IIS AspNetCore Module V2")
and (winlog.level:"error" or winlog.level:"warning")
```

---

## 7. Tuning & Best Practices

- **Noise control**: Drop specific noisy providers in `processors`.
- **Retention**: Use ILM to keep 30–90 days as needed.
- **Security**: Create a dedicated role in Elasticsearch with permissions only for `winlogbeat-*`.

---

✅ With this setup, all **Application/System error and warning events** registered in the Windows Event Log are continuously shipped and visible in a Kibana dashboard.
