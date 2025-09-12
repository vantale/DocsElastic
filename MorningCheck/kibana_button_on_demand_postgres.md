# On‑Demand Postgres Fetch from a “Button” in Kibana (Patterns A & B)

**Version:** v1.0 (2025‑09‑09)  
**Scope:** Kibana 8.x, Elasticsearch 8.x, Windows Server/any; Postgres 12+  
**Goal:** Click a button in Kibana → run SQL in Postgres → index results in Elasticsearch → see them immediately on the dashboard.

---

## TL;DR — Is this all I have to do?
Kibana **cannot** run SQL directly. You need a tiny **bridge** that runs the SQL and writes to Elasticsearch. Do **one** of the two setups below:

- **Pattern A (Webhook service):** Button → **your API** runs SQL → `_bulk` to ES. *(Cleanest if you can host a small service)*
- **Pattern B (Job queue in ES + Logstash):** Button creates a **job doc** in ES → **Logstash** polls jobs, runs SQL, indexes results. *(No custom API; pure Elastic + Logstash)*

This guide gives you **copy‑paste code/config** for both patterns **and** a Kibana UX with a button + filters.

---

## Architecture Overview

```
[User clicks button in Kibana]
    │
    ├─ Pattern A: URL drilldown → Webhook (FastAPI/Node) → Query Postgres → Bulk to ES
    │
    └─ Pattern B: Create job doc in ES (pg-jobs) → Logstash polls → JDBC query → Index to ES
```

**Result index:** `pg-report-*` (one doc per result row)  
**Jobs index (Pattern B):** `pg-jobs` (job metadata + status)  

---

## Prereqs & Security

- **Postgres read‑only user** with `SELECT` on needed tables/views only.
- Store secrets in **keystores** (`logstash-keystore`, environment variables) and use TLS.
- Avoid raw PII; prefer **views/materialized views** to minimize exposure.
- Lock down webhook/Logstash host network access to Postgres/ES.

---

## Pattern A — Webhook Service (FastAPI example)

### A1) Minimal FastAPI service

```python
# app.py
from fastapi import FastAPI, Header, HTTPException
from pydantic import BaseModel
from datetime import datetime
import os, psycopg2, requests, json

ES_URL = os.getenv("ES_URL", "https://es:9200")
ES_USER = os.getenv("ES_USER", "elastic")
ES_PWD  = os.getenv("ES_PWD", "changeme")
ES_INDEX = os.getenv("ES_INDEX", "pg-report-" + datetime.utcnow().strftime("%Y.%m.%d"))

PG_CONN = os.getenv("PG_CONN", "host=pg-host port=5432 dbname=mydb user=elastic_ro password=pgpass sslmode=require")
API_TOKEN = os.getenv("API_TOKEN", "SECRET-TOKEN")  # simple shared secret

app = FastAPI()

class ClientRequest(BaseModel):
    client_id: str
    limit: int = 500

@app.post("/run/client-report")
def run_report(req: ClientRequest, authorization: str = Header(None)):
    if authorization != f"Bearer {API_TOKEN}":
        raise HTTPException(status_code=401, detail="unauthorized")

    sql = """
      SELECT
        c.client_id, c.name,
        o.id AS order_id, o.amount, o.created_at
      FROM orders o
      JOIN clients c ON c.id = o.client_id
      WHERE c.client_id = %s
      ORDER BY o.created_at DESC
      LIMIT %s
    """

    conn = psycopg2.connect(PG_CONN)
    cur = conn.cursor()
    cur.execute(sql, (req.client_id, req.limit))
    rows = cur.fetchall()
    cols = [d[0] for d in cur.description]
    cur.close(); conn.close()

    # Prepare bulk payload
    bulk_lines = []
    for r in rows:
        doc = dict(zip(cols, r))
        doc["datasource"] = "postgres"
        doc["entity"] = "client_report"
        doc["@ingested_at"] = datetime.utcnow().isoformat() + "Z"
        bulk_lines.append(json.dumps({"index": {"_index": ES_INDEX}}))
        bulk_lines.append(json.dumps(doc, default=str))

    if not bulk_lines:
        return {"result_count": 0}

    bulk_payload = "\n".join(bulk_lines) + "\n"
    resp = requests.post(f"{ES_URL}/_bulk", data=bulk_payload, headers={"Content-Type":"application/x-ndjson"}, auth=(ES_USER, ES_PWD), timeout=60)
    resp.raise_for_status()

    return {"result_count": len(rows), "index": ES_INDEX}
```

**Run (example):**
```bash
pip install fastapi uvicorn psycopg2-binary requests
uvicorn app:app --host 0.0.0.0 --port 8080
```

**Env vars to set:**
```
ES_URL, ES_USER, ES_PWD, ES_INDEX (optional), PG_CONN, API_TOKEN
```

### A2) Kibana Button (URL Drilldown)

- Add a **Controls → Options list** for `client_id` bound to your main business index (or a thin “clients” index).
- Add a **Markdown** panel with a button‑like link:
  ```md
  [Fetch latest for selected client](https://webhook.yourdomain/run/client-report?client_id={{event.values.client_id}})
  ```
  If you need a `POST` with bearer, use a tiny proxy page or a Canvas custom element. Alternatively, open a link that triggers a server‑side POST.
- Turn on **Auto‑refresh** (e.g., 10s) so the dashboard updates after the webhook runs.
- Limit access via a reverse proxy that attaches `Authorization: Bearer SECRET-TOKEN` server‑side.

---

## Pattern B — ES Job Queue + Logstash (no API needed)

### B1) Create jobs index mapping
```json
PUT pg-jobs
{
  "mappings": {
    "properties": {
      "job_type":  { "type": "keyword" },
      "client_id": { "type": "keyword" },
      "status":    { "type": "keyword" },   // queued | running | done | failed
      "created_at": { "type": "date" },
      "completed_at": { "type": "date" },
      "result_count": { "type": "integer" }
    }
  }
}
```

### B2) Create a job document (your “button” action)
You need a way to **POST** this document. Options:
- Small internal proxy (recommended) called by a Kibana URL drilldown.
- Kibana **Webhook connector** from a rule (rule can be triggered manually).
- Curl / PowerShell shortcut for admins.

```bash
curl -u elastic:pwd -k -X POST "https://es:9200/pg-jobs/_doc" -H "Content-Type: application/json" -d '{
  "job_type":"client_report",
  "client_id":"ACME-123",
  "status":"queued",
  "created_at":"{{now}}"
}'
```

### B3) Logstash pipeline

`pipelines.yml`
```yaml
- pipeline.id: pg_job_runner
  path.config: "/etc/logstash/conf.d/pg_job_runner.conf"
```

`pg_job_runner.conf`
```conf
input {
  elasticsearch {
    hosts => ["https://es:9200"]
    user  => "${ES_USER}"
    password => "${ES_PWD}"
    index => "pg-jobs"
    schedule => "*/1 * * * *"   # every minute
    query => '{
      "size": 50,
      "_source": ["client_id","job_type","status","@timestamp"],
      "sort": [{"@timestamp":{"order":"asc"}}],
      "query": { "bool": { "filter": [
        {"term": {"job_type":"client_report"}},
        {"term": {"status":"queued"}}
      ]}}
    }'
    docinfo => true
  }
}

filter {
  if ![client_id] { drop { } }

  # mark as running
  mutate { add_field => { "status" => "running" } }

  # run the SQL for this client
  jdbc_streaming {
    jdbc_driver_library => "/usr/share/logstash/vendor/jar/postgresql-42.7.4.jar"
    jdbc_driver_class   => "org.postgresql.Driver"
    jdbc_connection_string => "jdbc:postgresql://pg-host:5432/mydb"
    jdbc_user => "elastic_ro"
    jdbc_password => "${PG_PWD}"
    statement => "
      SELECT
        c.client_id, c.name,
        o.id AS order_id, o.amount, o.created_at
      FROM orders o
      JOIN clients c ON c.id = o.client_id
      WHERE c.client_id = :client_id
      ORDER BY o.created_at DESC
      LIMIT 500
    "
    parameters => { "client_id" => "%%{client_id}" }
    target => "rows"
  }

  # split rows to individual documents
  split { field => "rows" }
  mutate {
    rename => { "rows" => "row" }
    add_field => {
      "datasource" => "postgres"
      "entity"     => "client_report"
      "job_client" => "%{client_id}"
    }
  }
  ruby { code => 'event.get("row").each { |k,v| event.set(k,v) }; event.remove("row")' }

  # compute a count for job update
  aggregate {
    task_id => "%{[@metadata][_id]}"
    code => "map['count'] ||= 0; map['count'] += 1;"
    push_previous_map_as_event => true
    timeout => 30
  }
}

output {
  # report rows (normal events)
  if [client_id] and [order_id] {
    elasticsearch {
      hosts => ["https://es:9200"]
      user  => "${ES_USER}"
      password => "${ES_PWD}"
      index => "pg-report-%{+YYYY.MM.dd}"
    }
  }

  # job status update (aggregated event only)
  if [count] {
    elasticsearch {
      hosts => ["https://es:9200"]
      user  => "${ES_USER}"
      password => "${ES_PWD}"
      action => "update"
      index  => "%{[@metadata][_index]}"
      document_id => "%{[@metadata][_id]}"
      doc_as_upsert => false
      script => 'ctx._source.status="done"; ctx._source.completed_at=Instant.now().toString(); ctx._source.result_count = params.c'
      script_params => { "c" => "%{[count]}" }
    }
  }
}
```

> Replace connection strings and credentials. Put secrets in the **Logstash keystore**:  
> `bin/logstash-keystore create` → `bin/logstash-keystore add ES_USER`, `ES_PWD`, `PG_PWD`

### B4) Kibana UX

- **Controls (Options list)** on `client_id` (from your business index or a static lookup).
- **Markdown** panel with a link to *enqueue* a job (via your proxy or admin link).
- **Saved search/table** showing the latest rows from `pg-report-*` filtered by the selected `client_id`.  
- **Saved search/table** for `pg-jobs` showing status (`queued/running/done`) for the selected client.
- Enable **Auto‑refresh** (e.g., 5–15s).

---

## Optional: Materialized Views (faster heavy queries)

If your report is expensive, create a **materialized view** that pre‑aggregates data. Cron a `REFRESH MATERIALIZED VIEW`, then ingest from the view in either Pattern A or B.

```sql
CREATE MATERIALIZED VIEW mv_client_report AS
SELECT
  c.client_id,
  c.name,
  COUNT(*)        AS orders_count,
  SUM(o.amount)   AS total_amount,
  MAX(o.created_at) AS last_order_at
FROM orders o
JOIN clients c ON c.id = o.client_id
GROUP BY c.client_id, c.name;

-- Periodic refresh (Linux cron/Windows task scheduler)
-- REFRESH MATERIALIZED VIEW CONCURRENTLY mv_client_report;
```

---

## Kibana Reporting & Alerts

- **PDF/PNG** exports from the dashboard (Reporting).  
- **Alerts** (Rules): notify if today’s orders for a client are zero, or if job failed (status `failed`, result_count=0).

---

## Checklist — “Is this all?”

1. Decide **Pattern A or B**.  
2. **Pattern A**: deploy the webhook (FastAPI/Node), secure it, create a **URL drilldown/button** in Kibana.  
3. **Pattern B**: create `pg-jobs` index, deploy the Logstash pipeline, create a **button** (proxy or admin link) that posts job docs.  
4. Create **Data Views** for `pg-report-*` (and `pg-jobs` if Pattern B).  
5. Build the dashboard: **client selector**, **results table**, **job status**, **auto‑refresh**.  
6. Add **Reporting/Alerts** if needed.  
7. Test with a known `client_id` and validate data freshness & counts.

That’s it—after this, your users press a button in Kibana and get fresh Postgres data on demand.

---

## Troubleshooting

- **Nothing shows after click** → Check webhook/Logstash logs; ensure ES auth/roles allow writes; dashboard is filtering the right index and `client_id` field.  
- **CORS/auth blocked** (Pattern A) → run the button through a server‑side proxy in your domain that attaches auth.  
- **Slow SQL** → switch to materialized view or limit `LIMIT N`, add indexes, tune execution plan.  
- **Duplicate rows** → set a **document_id** composite (e.g., client_id + order_id) when indexing.  
- **PII leakage** → use views to whitelist columns; mask where possible.

---

## Variations

- Use **Node.js/Express** instead of FastAPI.  
- Use **Canvas** with a custom workpad element for nicer button UX.  
- Use **Logstash jdbc** (non‑streaming) input to run scheduled pulls for *all* clients and then filter in Kibana (no button).

---

**End of document.**
