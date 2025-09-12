# Monitoring SQL Server Agent Jobs with Elastic Beats

## Goal
In Kibana, you can see:
- a list of jobs with their **latest result** (success/failure, time, message)
- **jobs currently running** (start time, duration)
- alerts when a job **fails** or **runs too long**

---

## Data Sources (choose 1–3)
1. **Metricbeat → `sql` module (driver: mssql)** – cleanest option: custom queries to `msdb`
2. **Winlogbeat → Windows Application Log** (source: `SQLSERVERAGENT`) – quick “plug-and-play”
3. **Filebeat → `SQLAGENT.OUT` log file** – when the agent writes logs to a file

Best observability in practice: (1) + (2).

---

## 0) SQL Account (SELECT-only for `msdb`)
```sql
CREATE LOGIN beat_reader WITH PASSWORD = 'Strong#Pass123!';
CREATE USER beat_reader FOR LOGIN beat_reader;
EXEC sp_addrolemember N'db_datareader', N'beat_reader'; -- in the msdb database
```

---

## 1) Metricbeat (SQL) – Last Job Result + Running Jobs

### T-SQL Queries

#### A) Last Result for Each Job
```sql
SELECT 
  j.job_id,
  j.name                AS job_name,
  h.run_status,
  CASE h.run_status 
    WHEN 0 THEN 'failed' 
    WHEN 1 THEN 'succeeded' 
    WHEN 2 THEN 'retry' 
    WHEN 3 THEN 'canceled' 
    WHEN 4 THEN 'in_progress' 
  END                  AS run_status_text,
  msdb.dbo.agent_datetime(h.run_date, h.run_time) AS last_run_at,
  h.run_duration,
  h.sql_severity,
  h.sql_message_id,
  h.message
FROM msdb.dbo.sysjobs j
JOIN msdb.dbo.sysjobhistory h 
  ON h.job_id = j.job_id
WHERE h.instance_id IN (
  SELECT MAX(h2.instance_id)
  FROM msdb.dbo.sysjobhistory h2
  WHERE h2.step_id = 0
  GROUP BY h2.job_id
);
```

#### B) Currently Running Jobs
```sql
SELECT 
  sja.job_id,
  j.name                        AS job_name,
  sja.start_execution_date      AS started_at,
  DATEDIFF(SECOND, sja.start_execution_date, SYSDATETIME()) AS running_seconds,
  sja.session_id,
  sja.stop_execution_date
FROM msdb.dbo.sysjobactivity sja
JOIN msdb.dbo.sysjobs j 
  ON j.job_id = sja.job_id
WHERE sja.start_execution_date IS NOT NULL
  AND sja.stop_execution_date  IS NULL;
```

### Metricbeat Config (`metricbeat.yml`)
```yaml
metricbeat.modules:
  - module: sql
    metricsets: ["query"]
    period: 60s
    hosts: ["sqlserver://localhost:1433?database=msdb&encrypt=false"]
    driver: "mssql"
    username: "beat_reader"
    password: "Strong#Pass123!"
    queries:
      - query: |
          SELECT 
            j.job_id, j.name AS job_name, h.run_status,
            CASE h.run_status 
              WHEN 0 THEN 'failed' 
              WHEN 1 THEN 'succeeded' 
              WHEN 2 THEN 'retry' 
              WHEN 3 THEN 'canceled' 
              WHEN 4 THEN 'in_progress' 
            END AS run_status_text,
            msdb.dbo.agent_datetime(h.run_date, h.run_time) AS last_run_at,
            h.run_duration, h.sql_severity, h.sql_message_id, h.message
          FROM msdb.dbo.sysjobs j
          JOIN msdb.dbo.sysjobhistory h ON h.job_id = j.job_id
          WHERE h.instance_id IN (
            SELECT MAX(h2.instance_id) FROM msdb.dbo.sysjobhistory h2
            WHERE h2.step_id = 0 GROUP BY h2.job_id
          );
        fields:
          event.dataset: "mssql.jobs_last_status"
        fields_under_root: true

      - query: |
          SELECT 
            sja.job_id, j.name AS job_name,
            sja.start_execution_date AS started_at,
            DATEDIFF(SECOND, sja.start_execution_date, SYSDATETIME()) AS running_seconds,
            sja.session_id
          FROM msdb.dbo.sysjobactivity sja
          JOIN msdb.dbo.sysjobs j ON j.job_id = sja.job_id
          WHERE sja.start_execution_date IS NOT NULL
            AND sja.stop_execution_date  IS NULL;
        fields:
          event.dataset: "mssql.jobs_running"
        fields_under_root: true

output.elasticsearch:
  hosts: ["http://ELASTIC:9200"]

setup.template.enabled: true
setup.ilm.enabled: true
```

---

## 2) Winlogbeat – SQL Server Agent Event Logs
```yaml
winlogbeat.event_logs:
  - name: Application
    providers: ["SQLSERVERAGENT"]

processors:
  - add_fields:
      target: ''
      fields:
        event.dataset: "mssql.agent_eventlog"

output.elasticsearch:
  hosts: ["http://ELASTIC:9200"]
```

---

## 3) Filebeat – `SQLAGENT.OUT` Log File (Optional)
```yaml
filebeat.inputs:
  - type: filestream
    id: sqlagent_out
    enabled: true
    paths:
      - "C:/Program Files/Microsoft SQL Server/MSSQL*/MSSQL/Log/SQLAGENT.OUT*"
    parsers:
      - multiline:
          type: pattern
          pattern: '^\d{4}-\d{2}-\d{2}'
          negate: true
          match: after
    fields:
      event.dataset: "mssql.agent_filelog"
    fields_under_root: true

output.elasticsearch:
  hosts: ["http://ELASTIC:9200"]
```

---

## Kibana: Views + Alerts

### Data Views
- `metricbeat-*` (for `mssql.jobs_last_status` and `mssql.jobs_running`)
- `winlogbeat-*` (optional)
- `filebeat-*` (optional)

### Lens / Dashboard Ideas
- **Table: “Job status (last)”**
  - Rows: `job_name`
  - Columns: `run_status_text` (Top value), `last_run_at` (Max), `message` (Top value)
  - Filter: `event.dataset: "mssql.jobs_last_status"`
- **Chart: “Jobs running now”**
  - Table with `job_name`, `running_seconds` (Max)
  - Filter: `event.dataset: "mssql.jobs_running"`

### Alerts
1. **Job failed**
   - KQL: `event.dataset: "mssql.jobs_last_status" AND run_status_text: "failed"`
   - Schedule: every 1–5 minutes, deduplicate by `job_id`
2. **Running too long**
   - KQL: `event.dataset: "mssql.jobs_running" AND running_seconds >= 3600`

---

## Best Practices
- Use `beat_reader` with only `db_datareader` rights in `msdb`
- Optionally map to ECS fields (`event.category`, `event.action`, `event.outcome`)
- Consider a Transform to keep only the latest status per job
- Heartbeat can complement this for port/endpoint monitoring

---

## Minimal “For Tomorrow” Plan
1. Deploy Winlogbeat with `SQLSERVERAGENT` provider
2. Add Metricbeat SQL module with above queries
3. Create two alert rules and a “Job status” table in Kibana
