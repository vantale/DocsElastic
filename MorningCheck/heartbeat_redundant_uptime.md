# Reliable Uptime Monitoring with Redundant Heartbeat Agents (Windows Server)

This tutorial shows how to **monitor Windows Servers continuously** using **multiple Heartbeat agents** (Elastic Stack) so that:
- Each target is checked by **≥2 independent Heartbeats**.
- All results go to the **same Elasticsearch/Kibana**.
- Dashboards show **global availability** (“up if *any* observer sees it up”) **and** **per‑observer** details.
- You also get alerts when a **Heartbeat agent itself goes silent** (“monitor the monitor”).

> Works for both **standalone Heartbeat (ZIP)** and **Elastic Agent (Fleet)**. Steps focus on Windows Server (no Docker).

---

## 0) Prerequisites & Notation

- **Elastic Stack 8.x** (Elasticsearch + Kibana).
- Network access from each **monitor node** (the server running Heartbeat) to targets you check.
- Time synchronized (NTP). **Clock skew** breaks uptime charts.
- You’ll deploy Heartbeat on at least **two different machines** (e.g., `HB-EUW` and `HB-EUE`).

Terminology (ECS fields you’ll see in Kibana):
- `monitor.id`: your stable ID for a check (must be the same across redundant Heartbeats).
- `observer.name`: the **monitor node** name (must be **different** per Heartbeat instance).
- `monitor.status`: `up` / `down` per check execution.
- `event.dataset: "uptime"` or `data_stream.dataset: "uptime"` (Agent).

---

## 1) Design the Topology

**Goal:** Avoid single points of failure (SPOF).

- Pick **2+ Heartbeat nodes** on **different** hosts / racks / subnets (e.g., `HB-EUW` and `HB-EUE`).
- Both run **the same monitors** (same `monitor.id`) to the **same targets**.
- Both send to the **same Elasticsearch** (same index / data stream).

Result:
- If `HB-EUW` goes offline, `HB-EUE` still checks the targets.
- You can also detect **partial outages** (DNS, firewall) by comparing observers.

> Optional: add a **3rd node off‑site** (e.g., cloud VM) to validate from the Internet side.

---

## 2) Prepare Consistent IDs and Names

Create a small table for your monitors and observers:

| Purpose            | Value (example)               |
|--------------------|-------------------------------|
| Heartbeat #1 name  | `HB-EUW`                      |
| Heartbeat #2 name  | `HB-EUE`                      |
| Monitor ID (RDP)   | `rdp-win-y`                   |
| Monitor ID (ICMP)  | `icmp-win-y`                  |
| Monitor ID (HTTP)  | `http-iis-y`                  |

Rules:
- **Same** `monitor.id` across agents for the **same target** (so Kibana can aggregate them).
- **Different** `observer.name` per agent (so you can split by location).
- Stagger schedules by a few seconds to avoid thundering herd (e.g., `schedule: "@every 30s"` but shifted using different `ssl.verification_mode` or tag + `schedule` offsets—see §6).

---

## 3) Standalone Heartbeat on Windows (ZIP)

### 3.1 Install on both nodes
1. Download Heartbeat ZIP for Windows from Elastic and unzip to `C:\Elastic\heartbeat\`.
2. Open **PowerShell (Admin)**, go to the Heartbeat folder:
   ```powershell
   cd C:\Elastic\heartbeat
   ```
3. (Optional) Install as a Windows Service with **NSSM** or use built‑in service installer if provided by your version (Elastic Agent has built‑in; Heartbeat OSS often uses NSSM).  
   Example with NSSM:
   ```powershell
   nssm install Heartbeat-EUW "C:\Elastic\heartbeat\heartbeat.exe" "-c" "C:\Elastic\heartbeat\heartbeat.yml"
   nssm set Heartbeat-EUW AppDirectory "C:\Elastic\heartbeat"
   nssm set Heartbeat-EUW AppRestartDelay 5000
   nssm start Heartbeat-EUW
   ```

> Repeat on the second node (`HB-EUE`) with its own service name.

### 3.2 Configure `heartbeat.yml` on each node

On **HB‑EUW** (observer.name = HB-EUW):
```yaml
# C:\Elastic\heartbeat\heartbeat.yml
heartbeat.monitors:
  - type: tcp
    id: rdp-win-y              # <-- stable monitor.id shared across agents
    name: "RDP to WIN-Y"
    hosts: ["win-y.example.local:3389"]
    schedule: "@every 30s"
    fields:
      service: "rdp"
    tags: ["win", "rdp", "redundant"]

  - type: icmp
    id: icmp-win-y
    name: "ICMP to WIN-Y"
    hosts: ["win-y.example.local"]
    schedule: "@every 30s"
    tags: ["win", "icmp", "redundant"]

  - type: http
    id: http-iis-y
    name: "HTTP IIS on WIN-Y"
    urls: ["http://win-y.example.local/health"]
    schedule: "@every 30s"
    check.response.status: [200]
    tags: ["win", "http", "redundant"]

# Identify this monitor node
name: HB-EUW                 # <-- observer.name in events
fields:
  observer_site: "EU-West"   # optional: custom field to group observers

# Output to Elasticsearch
output.elasticsearch:
  hosts: ["https://es1.local:9200","https://es2.local:9200"]
  username: "heartbeat_writer"
  password: "${ES_PWD}"
  ssl.certificate_authorities: ["C:/Elastic/certs/ca.crt"]

logging:
  level: info
  to_files: true
  files:
    path: C:/Elastic/heartbeat/logs
```

On **HB‑EUE** (observer.name = HB-EUE) use **the same monitors** (`id` values identical), but change:
```yaml
name: HB-EUE
fields:
  observer_site: "EU-East"
```

> ✅ **Key:** `id` must match across agents for redundancy. `name`/`fields` identify the observer.

Restart services after editing `heartbeat.yml`.

---

## 4) Elastic Agent (Fleet) Alternative

If you prefer Fleet:
1. In **Kibana → Fleet → Integrations**, add **Uptime** (Heartbeat) policy with your monitors.
2. Assign this policy to **two different Elastic Agents** on your observer nodes.
3. In YAML advanced settings, set:
   - identical `id` for each monitor,
   - distinct `observer_location`/`observer.name` via `name` or agent metadata,
   - same schedules (or slightly staggered via multiple monitor entries).

Fleet will ship to data streams `logs-uptime*` automatically.

---

## 5) Build Kibana Views (Lens/Uptime App)

### 5.1 Quick wins in the **Uptime** app
- Uptime (Kibana) automatically groups by `monitor.id` and **Location** (observer).
- You’ll see **redundant checks** per location.
- Use **Overview** for quick status and **Monitor Detail** for history and pings.

### 5.2 “Up if **any observer** is up” (Lens formula)
Create a **Lens** visualization (Line/Area or Metric) over `heartbeat-*` or `logs-uptime*`:

- **Filters**: `monitor.id: rdp-win-y`
- **Metric (per time bucket)** = `max( is_up )` where `is_up = if(equals(params.status, "up"), 1, 0)` aggregated over all observers.

In **Lens → Formula**, use:
```
max(
  if(
    match(field="monitor.status", query="up"),
    1,
    0
  )
)
```
- **Break down by**: `monitor.id` (terms) to show many monitors at once.
- Interpretation: If **≥1** observer reports `up`, the max = 1 (global up). If **all** report `down`, max = 0 (global down).

> Tip: Add a small multiple or a separate chart **split by `observer.name`** to debug location-specific issues.

### 5.3 Availability % (global and per observer)
**Global availability (any observer):**
```
100 * average(
  if(match(field="monitor.status", query="up"), 1, 0)
)
```
- This average across **all observers** tells you “percent of time where at least one observer saw it up”.  
  (Set **time interval** to your desired SLO granularity, e.g., 1m/5m/1h.)

**Per‑observer availability:** same formula but **break down by `observer.name`** to see each node’s perspective.

---

## 6) Reduce Noise & Collisions

- **Stagger schedules**: if both agents run `@every 30s` at the same second, their pings collide.
  - Option A: Set one to `@every 30s` and the other to `@every 31s`.
  - Option B: Use slightly different monitor lists or add a dummy `timeout` difference.
- **Timeouts**: Use reasonable `timeout` (e.g., 3–5s) so a brief hiccup doesn’t cause prolonged blocking.
- **Retries**: For HTTP/TCP, consider `check.request`/`mode: any` (version dependent) or short intervals + alert dampening instead of high retries.
- **DNS**: Consider hard‑coding target IPs for ICMP/TCP monitors if DNS is flaky (or monitor DNS separately).

---

## 7) Alerts

### 7.1 Target down (global)
Create a **Kibana rule** (Uptime rule or ES query) that triggers when **all observers** report `down` for a monitor.

**Using Uptime rule (“Monitor status”):**
- Condition: `Monitor: rdp-win-y`
- Locations: **All**
- Status: **Down**
- For **X time** (e.g., 1 minute)
- Action: email/Slack/etc.

> Uptime understands monitors + locations and will only fire when **no location** reports `up`.

### 7.2 “Monitor the monitors” (Heartbeat agent silent)
Alert if **no events from a specific observer** arrive within 2 minutes:

**Rule type:** *Elasticsearch query*  
**Index:** `heartbeat-*` (or `logs-uptime*`)  
**KQL condition:**  
```
observer.name: "HB-EUW" and @timestamp > now()-2m
```
**Throttle / schedule:** every 1 minute  
**Action:** “Heartbeat on HB-EUW is not reporting (agent down or network issue).”

Alternative (Log threshold / Missing data): Use a **“no data”** alert if your Stack version supports it for logs data views.

---

## 8) Dashboards (Starter Pack)

Create a new **Dashboard** and add:

1) **Global status (any observer)** – Lens Metric  
- Formula:  
  ```
  round( 100 * average( if(match(field="monitor.status", query="up"), 1, 0) ) , 1 )
  ```
- Filter: `monitor.id: *`
- Time: Last 24h

2) **Per‑observer status** – Lens Bar (split by `observer.name`)  
- Same formula as above; **Break down by:** `observer.name`
- Filter by a specific `monitor.id` with a control.

3) **Timeline (any observer is up)** – Lens Area  
- Formula: `max( if(match(field="monitor.status","up"),1,0) )`
- Break down by `monitor.id` (small multiples for many checks).

4) **Ping latency** – Lens Line  
- Field: `monitor.duration.us` (or `monitor.duration.ms` depending on version)
- Break down by `observer.name`.

5) **Top “noisiest” monitors** – Lens Table  
- Columns: `monitor.id`, `down events (count)`, `availability %`.
- Sort by `down events` desc.

---

## 9) Operational Tips

- **Version alignment**: Keep Heartbeat versions close to ES/Kibana.
- **Certificates**: Use CA pinning; rotate credentials using keystores or ENV vars (`${ES_PWD}`).
- **Resource limits**: Tune concurrency if you monitor many endpoints (`heartbeat.scheduler` options vary by version).
- **Service hardening**: Run services with least privilege; auto‑restart on failure.
- **Retention**: ILM policy for `heartbeat-*` (e.g., hot 7d → warm 30d → delete 90d).

---

## 10) Troubleshooting Checklist

- No data? Check:
  - Service status on each observer (`nssm status Heartbeat-EUW`).
  - Network reachability from observer to target.
  - TLS trust chain (`ssl.certificate_authorities`).
  - **IDs**: Same `monitor.id` across agents? Different `observer.name`?
- Spiky graphs?
  - Stagger schedules; reduce interval to 30s–60s with proper alert dampening.
- False downs?
  - Increase `timeout` slightly; verify DNS and route asymmetry.
- Too many docs?
  - Increase interval to 30–60s; restrict per‑observer checks to what you really need.

---

## 11) Minimal Copy‑Paste Set

### 11.1 `heartbeat.yml` (observer A – HB‑EUW)
```yaml
heartbeat.monitors:
  - type: icmp
    id: icmp-win-y
    name: "ICMP to WIN-Y"
    hosts: ["win-y.example.local"]
    schedule: "@every 30s"
    timeout: 3s
    tags: ["redundant"]

  - type: tcp
    id: rdp-win-y
    name: "RDP to WIN-Y"
    hosts: ["win-y.example.local:3389"]
    schedule: "@every 30s"
    timeout: 3s
    tags: ["redundant"]

  - type: http
    id: http-iis-y
    name: "HTTP IIS on WIN-Y"
    urls: ["http://win-y.example.local/health"]
    schedule: "@every 30s"
    check.response.status: [200]
    timeout: 3s
    tags: ["redundant"]

name: HB-EUW
fields:
  observer_site: "EU-West"

output.elasticsearch:
  hosts: ["https://es1.local:9200"]
  username: "heartbeat_writer"
  password: "${ES_PWD}"
  ssl.certificate_authorities: ["C:/Elastic/certs/ca.crt"]
```

### 11.2 `heartbeat.yml` (observer B – HB‑EUE)
```yaml
# Same monitors with same IDs; different observer "name"
heartbeat.monitors:
  - type: icmp
    id: icmp-win-y
    hosts: ["win-y.example.local"]
    schedule: "@every 31s"
    timeout: 3s

  - type: tcp
    id: rdp-win-y
    hosts: ["win-y.example.local:3389"]
    schedule: "@every 31s"
    timeout: 3s

  - type: http
    id: http-iis-y
    urls: ["http://win-y.example.local/health"]
    schedule: "@every 31s"
    check.response.status: [200]
    timeout: 3s

name: HB-EUE
fields:
  observer_site: "EU-East"

output.elasticsearch:
  hosts: ["https://es1.local:9200"]
  username: "heartbeat_writer"
  password: "${ES_PWD}"
  ssl.certificate_authorities: ["C:/Elastic/certs/ca.crt"]
```

### 11.3 Lens formula snippets

**Any‑observer up (per time bucket):**
```
max(if(match(field="monitor.status","up"),1,0))
```

**Availability % (bucketed):**
```
100 * average(if(match(field="monitor.status","up"),1,0))
```

---

## 12) Summary

- Deploy **two+ Heartbeats** on different machines (distinct `observer.name`).
- Use **identical `monitor.id`** to denote the **same target** across observers.
- Send all results to the **same cluster**.
- In Kibana, compute **global up = max(any‑observer‑up)** and also show **per‑observer** views.
- Add alerts for **global down** and **agent silent**.
- Stagger schedules, tune timeouts, and keep clocks in sync.

You now have a **fault‑tolerant uptime** system without a single monitoring SPOF.
