# Monitoring API Healthchecks and JSON Endpoints with Elastic Stack

This guide explains how to monitor API endpoints where some return simple healthcheck responses (`"OK"`) and others return JSON content. We use **Heartbeat**, **Ingest Pipelines**, and **Kibana Dashboards**.

---

## 1. Heartbeat Configuration (HTTP Monitors)

Use Heartbeat to capture status codes and bodies. Configure monitors for both simple healthchecks and JSON endpoints.

```yaml
# heartbeat.yml
heartbeat.monitors:
  # A) Simple healthcheck: "OK" in body + 200
  - type: http
    id: api-health
    name: API /health
    urls: ["https://api.example.com/health"]
    schedule: "@every 30s"
    response.include_body: always
    check.response:
      status: 200
      body: "OK"
    tags: ["env:prod","kind:health"]

  # B) JSON endpoint: 200 and JSON field assertions
  - type: http
    id: api-status
    name: API /status
    urls: ["https://api.example.com/status"]
    schedule: "@every 30s"
    response.include_body: always
    check.request:
      method: GET
      headers:
        Accept: application/json
    check.response:
      status: 200
      json:
        - description: status is ok
          condition:
            equals:
              status: "ok"
        - description: version present
          condition:
            is:
              version: "*"
    tags: ["env:prod","kind:json"]
```

---

## 2. Ingest Pipeline (Parse Response Body)

Create an ingest pipeline that normalizes JSON bodies and simple `"OK"` responses into structured fields.

```json
PUT _ingest/pipeline/heartbeat_api_parser
{
  "processors": [
    {
      "set": {
        "if": "ctx.http?.response?.body?.contents != null",
        "field": "observed.endpoint",
        "value": "{{url.full}}"
      }
    },
    {
      "json": {
        "if": "ctx.http?.response?.headers?.['content-type'] != null && ctx.http.response.headers['content-type'].toLowerCase().contains('application/json') && ctx.http?.response?.body?.contents != null",
        "field": "http.response.body.contents",
        "target_field": "api.body",
        "ignore_failure": true
      }
    },
    {
      "set": {
        "if": "ctx.http?.response?.body?.contents != null && ctx.http.response.body.contents.trim() == 'OK'",
        "field": "api.health_ok",
        "value": true
      }
    },
    {
      "set": {
        "if": "ctx.api?.health_ok == null",
        "field": "api.health_ok",
        "value": false
      }
    },
    {
      "set": {
        "if": "ctx.api?.body?.status != null",
        "field": "api.status",
        "value": "{{api.body.status}}"
      }
    },
    {
      "set": {
        "if": "ctx.api?.body?.version != null",
        "field": "api.version",
        "value": "{{api.body.version}}"
      }
    }
  ]
}
```

Point Heartbeat to this pipeline:

```yaml
output.elasticsearch:
  hosts: ["https://your-es:9200"]
  pipeline: heartbeat_api_parser
```

---

## 3. Dashboards in Kibana (Uptime + Lens)

Once data is parsed, you can create visualizations:

- **Availability**: Percentage of `monitor.status: "up"` by monitor name.
- **Healthchecks**: Count where `api.health_ok: true` grouped by `monitor.name`.
- **Status distribution**: Terms aggregation on `api.status`.
- **Version drift**: Top values of `api.version` across environments.
- **Latency**: P95 of `http.rtt.total.us` (convert to ms).

**Tips:**
- Use Uptime app for out-of-the-box availability and latency.
- Add alerts like “Monitor status is down” or “Availability < 99.5%”.
- Tag monitors with `env`, `service`, or `team` for filtering.

---

## 4. Summary

- **Heartbeat** captures endpoint responses.
- **Ingest Pipeline** parses JSON or `"OK"` into structured fields.
- **Kibana** dashboards aggregate health, latency, status, and version.

This setup lets you monitor both simple healthchecks and JSON APIs in a consistent, visual way.
