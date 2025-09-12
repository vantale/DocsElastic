# Monitoring Windows Services with Elastic Stack (Without Elastic Agent)

This guide explains how to check if specific Windows Services are running using **Metricbeat** (without Elastic Agent).

---

## 1. Install Metricbeat on Windows
1. Download Metricbeat ZIP (same version as your Elasticsearch/Kibana).
2. Unzip to e.g. `C:\Elastic\metricbeat`.
3. Open **PowerShell (Admin)** in that folder.

---

## 2. Configure Metricbeat to Read Windows Services
Edit `metricbeat.yml`:

```yaml
metricbeat.modules:
  - module: windows
    metricsets: ["service"]
    period: 30s

    # Keep only the services you care about
    processors:
      - drop_event:
          when:
            not:
              or:
                - equals: { windows.service.name: "Spooler" }
                - equals: { windows.service.name: "W32Time" }
                - equals: { windows.service.name: "Dnscache" }

output.elasticsearch:
  hosts: ["https://YOUR-ELASTIC:9200"]
  username: "metricbeat_writer"
  password: "YOUR_PASSWORD"
  ssl.verification_mode: certificate

setup.kibana:
  host: "https://YOUR-KIBANA:5601"
```

---

## 3. Install & Start Metricbeat as a Service
```powershell
# From C:\Elastic\metricbeat
.\metricbeat.exe test config -c .\metricbeat.yml -e
.\metricbeat.exe setup -c .\metricbeat.yml -e      # optional (needs Kibana)
.\install-service-metricbeat.ps1
Start-Service metricbeat
Get-Service metricbeat
```

### What You’ll Ingest
Index: `metricbeat-*`  
Fields: `event.dataset: "windows.service"`, `windows.service.name`, `windows.service.display_name`, `windows.service.state` (`running|stopped|paused`).

---

## 4. Quick Checks in Kibana

### A) KQL (Discover)
```kql
event.dataset: "windows.service" 
and windows.service.name: ("Spooler","W32Time","Dnscache")
```

### B) Latest State per Host/Service (ES|QL)
```esql
FROM metricbeat-*
| WHERE event.dataset == "windows.service"
  AND windows.service.name IN ("Spooler","W32Time","Dnscache")
| STATS last_seen = MAX(@timestamp),
        last_state = ANY_VALUE(windows.service.state)
  BY host.name, windows.service.name
| SORT host.name, windows.service.name
```

---

## 5. Create an Alert When a Service Isn’t Running
1. Go to **Kibana → Alerts → Create rule → Query**.
2. Configure:
   - **Index**: `metricbeat-*`
   - **KQL**:
     ```kql
     event.dataset: "windows.service"
     and windows.service.name: ("Spooler","W32Time","Dnscache")
     and windows.service.state: * and windows.service.state != "running"
     ```
   - **Group by**: `host.name`, `windows.service.name`
   - **Schedule**: every 1–5 minutes
   - **Action message**:
     ```
     Service {{context.groupBy "windows.service.name"}} on {{context.groupBy "host.name"}} is {{state.values."windows.service.state"}} at {{date}}.
     ```

---

## 6. (Optional) Add Service Start/Stop History
If you also want start/stop events:

- Install **Winlogbeat**.
- Collect Event IDs **7036** (service entered running/stopped) and **7034** (terminated unexpectedly).

```yaml
winlogbeat.event_logs:
  - name: System
    ignore_older: 72h
    event_id: 7036, 7034

output.elasticsearch:
  hosts: ["https://YOUR-ELASTIC:9200"]
  username: "winlogbeat_writer"
  password: "YOUR_PASSWORD"
```

---

## 7. Verify via API (PowerShell)
```powershell
$es = "https://YOUR-ELASTIC:9200"
$auth = "YOUR_USER:YOUR_PASSWORD"

$body = @"
{
  "size": 0,
  "query": {
    "bool": {
      "filter": [
        {"term": {"event.dataset": "windows.service"}},
        {"terms": {"windows.service.name": ["Spooler","W32Time","Dnscache"]}},
        {"range": {"@timestamp": {"gte": "now-10m"}}}
      ]
    }
  },
  "aggs": {
    "by_host": {
      "terms": {"field": "host.name", "size": 100},
      "aggs": {
        "by_service": {
          "terms": {"field": "windows.service.name.keyword", "size": 50},
          "aggs": {
            "latest": {
              "top_hits": {
                "sort": [{"@timestamp": {"order": "desc"}}],
                "_source": {"includes": ["windows.service.state","@timestamp"]},
                "size": 1
              }
            }
          }
        }
      }
    }
  }
}
"@

curl -u $auth -k "$es/metricbeat-*/_search" -H "Content-Type: application/json" -d $body
```

---

## 8. Troubleshooting
- **No docs?** Check `metricbeat.exe -e` logs.
- **TLS/auth issues?** Confirm creds/CA.
- **Time skew?** Sync system clock.
- **Service names**: Use `Get-Service | Select Name,DisplayName` to confirm.

---

## ✅ TL;DR
- Install Metricbeat → enable `windows/service` metricset.  
- Ingest service state into `metricbeat-*`.  
- Use Kibana Lens/ES|QL to check.  
- Create an alert when `state != running`.  
- Optionally, add Winlogbeat for event history.  

