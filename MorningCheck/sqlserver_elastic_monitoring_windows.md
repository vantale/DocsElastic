# SQL Server Throughput & Query Performance Monitoring with ElasticStack (Windows Server)

**Version:** v1.0 (2025‑09‑09)  
**Applies to:** Windows Server 2016/2019/2022, SQL Server 2016+ (incl. Express), Elastic 8.x (Metricbeat/Winlogbeat/Kibana)

---

## 🎯 Objectives

- Measure **throughput** (batches/sec, transactions/sec)  
- Track **query latency** and show **Top 10 longest** (current + historical)  
- Inspect **SQL text** for slow queries  
- Detect **deadlocks** & **blocking chains**  
- Add **wait stats**, **I/O**, **TempDB**, **log usage**, **index usage**  
- Visualize in **Kibana** and set **alerts**

---

## 🔐 Prerequisites & Least‑Privilege SQL Login

> Many DMVs require `VIEW SERVER STATE`. Create a dedicated read‑only login with only what’s needed.

```sql
-- 1) Create a SQL login + server user
CREATE LOGIN elastic_ro WITH PASSWORD = 'ChangeMe_Complex#2025', CHECK_POLICY = ON, CHECK_EXPIRATION = ON;
CREATE USER elastic_ro FOR LOGIN elastic_ro;

-- 2) Grant DMV access for server-wide visibility
GRANT VIEW SERVER STATE TO elastic_ro;

-- 3) For database size / index stats (scoped)
--   Run per user database if you want granular permissions
USE YourDatabaseName;
CREATE USER elastic_ro FOR LOGIN elastic_ro; -- no-op if already exists in db
EXEC sp_addrolemember 'db_datareader', 'elastic_ro';

-- Optional (for DB-scoped DMVs):
GRANT VIEW DATABASE STATE TO elastic_ro;
```

> **Security tips**
> - Use a **low-privilege** account per environment.  
> - Restrict from write/DDL roles.  
> - Rotate the password and store it in **Metricbeat keystore** (`metricbeat keystore add sqlserver_pwd`).

---

## 🧩 Architecture Overview

- **Metricbeat**
  - **Windows/perfmon**: classic SQL perf counters (batches/sec, deadlocks/sec, buffer hit ratio)
  - **SQL module (driver: mssql)**: custom DMV queries (Top N, waits, blocking, TempDB, index usage, file I/O)
  - **MSSQL module (optional)**: built-in metrics (perf/db/log)
- **Winlogbeat**
  - From **Windows Application Log** (`MSSQLSERVER` source) → **deadlocks (1205)**, errors, timeouts
  - Optional: **SQL Server Error Log** file input if configured to log deadlock graphs
- **Kibana**
  - **Lens dashboards** (Top 10, throughput, deadlocks) + **Alerting**

---

## ⚙️ Metricbeat — Windows PerfMon counters

Create `modules.d/windows.yml` (or a dedicated file) and enable the **perfmon** metricset:

```yaml
- module: windows
  metricsets: ["perfmon"]
  period: 10s
  perfmon.counters:
    - instance_label: "batches_per_sec"
      query: '\SQLServer:SQL Statistics\Batch Requests/sec'
    - instance_label: "tx_per_sec"
      query: '\SQLServer:Databases(_Total)\Transactions/sec'
    - instance_label: "deadlocks_per_sec"
      query: '\SQLServer:Locks(_Total)\Number of Deadlocks/sec'
    - instance_label: "buffer_hit_ratio"
      query: '\SQLServer:Buffer Manager\Buffer cache hit ratio'
    - instance_label: "page_life_expectancy"
      query: '\SQLServer:Buffer Manager\Page life expectancy'
    - instance_label: "user_connections"
      query: '\SQLServer:General Statistics\User Connections'
```

> If your instance uses a named instance (e.g. `MSSQL$DEV`), PerfMon object names sometimes include it. Validate the exact object path with **perfmon.exe** → Add Counters.

---

## ⚙️ Metricbeat — SQL module (custom DMV queries)

Enable the **sql** module with the **mssql** driver to pull rich query diagnostics. Create `modules.d/sql.yml`:

```yaml
- module: sql
  metricsets: ["query"]
  period: 30s

  hosts:
    - "mssql://elastic_ro:${SQLSERVER_PWD}@YOUR_SQL_HOST:1433?database=master&encrypt=true&trustservercertificate=true"

  sql_queries:
    # 1) Top 10 currently running requests by elapsed time
    - query: |
        SELECT TOP 10
          r.session_id,
          r.status,
          r.command,
          r.cpu_time AS cpu_ms,
          r.total_elapsed_time AS elapsed_ms,
          r.reads, r.writes,
          r.blocking_session_id,
          DB_NAME(r.database_id) AS database_name,
          wt.wait_type, wt.wait_duration_ms,
          SUBSTRING(qt.text, (r.statement_start_offset/2)+1,
            ((CASE r.statement_end_offset WHEN -1 THEN DATALENGTH(qt.text) ELSE r.statement_end_offset END - r.statement_start_offset)/2)+1) AS statement_text,
          qt.text AS full_text
        FROM sys.dm_exec_requests r
        OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) qt
        LEFT JOIN sys.dm_os_waiting_tasks wt ON r.session_id = wt.session_id
        WHERE r.session_id <> @@SPID
        ORDER BY r.total_elapsed_time DESC;
      label: "sqlserver_top10_running"

    # 2) Historical Top 10 by avg elapsed time (from plan cache)
    - query: |
        SELECT TOP 10
          CAST(qs.total_elapsed_time / NULLIF(qs.execution_count,0) AS BIGINT) AS avg_elapsed_ms,
          CAST(qs.total_worker_time / NULLIF(qs.execution_count,0) AS BIGINT) AS avg_cpu_ms,
          CAST(qs.total_logical_reads / NULLIF(qs.execution_count,0) AS BIGINT) AS avg_reads,
          CAST(qs.total_logical_writes / NULLIF(qs.execution_count,0) AS BIGINT) AS avg_writes,
          qs.execution_count,
          qs.last_execution_time,
          SUBSTRING(qt.text, (qs.statement_start_offset/2)+1,
            ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(qt.text) ELSE qs.statement_end_offset END - qs.statement_start_offset)/2)+1) AS statement_text,
          qt.text AS full_text
        FROM sys.dm_exec_query_stats qs
        CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) qt
        ORDER BY avg_elapsed_ms DESC;
      label: "sqlserver_top10_historical"

    # 3) Blocking sessions (who is blocking whom)
    - query: |
        SELECT
          r.session_id,
          r.blocking_session_id,
          r.wait_type,
          r.wait_time AS wait_time_ms,
          DB_NAME(r.database_id) AS database_name,
          SUBSTRING(qt.text, (r.statement_start_offset/2)+1,
            ((CASE r.statement_end_offset WHEN -1 THEN DATALENGTH(qt.text) ELSE r.statement_end_offset END - r.statement_start_offset)/2)+1) AS statement_text
        FROM sys.dm_exec_requests r
        OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) qt
        WHERE r.blocking_session_id <> 0
        ORDER BY r.wait_time DESC;
      label: "sqlserver_blocking"

    # 4) Deadlock rate via DMV (perf counter mirror)
    - query: |
        SELECT
          instance_name,
          cntr_value AS deadlocks_per_sec
        FROM sys.dm_os_performance_counters
        WHERE counter_name = 'Number of Deadlocks/sec'
          AND instance_name = '_Total';
      label: "sqlserver_deadlocks_per_sec"

    # 5) Top waits (what the server is waiting on)
    - query: |
        SELECT TOP 20
          wait_type,
          waiting_tasks_count,
          wait_time_ms,
          signal_wait_time_ms
        FROM sys.dm_os_wait_stats
        WHERE wait_type NOT LIKE 'SLEEP%'
          AND wait_type NOT LIKE 'BROKER_TASK_STOP%'
        ORDER BY wait_time_ms DESC;
      label: "sqlserver_waits"

    # 6) TempDB usage (per DB)
    - query: |
        SELECT
          DB_NAME(database_id) AS database_name,
          SUM((user_object_reserved_page_count + internal_object_reserved_page_count) * 8) AS tempdb_space_kb
        FROM sys.dm_db_file_space_usage
        GROUP BY database_id;
      label: "sqlserver_tempdb_usage"

    # 7) Database file I/O latency
    - query: |
        SELECT DB_NAME(vfs.database_id) AS database_name,
               mf.name AS file_name,
               vfs.io_stall_read_ms / NULLIF(vfs.num_of_reads,0)  AS avg_read_ms,
               vfs.io_stall_write_ms / NULLIF(vfs.num_of_writes,0) AS avg_write_ms,
               vfs.num_of_reads, vfs.num_of_writes
        FROM sys.dm_io_virtual_file_stats(NULL, NULL) vfs
        JOIN sys.master_files mf ON vfs.database_id = mf.database_id AND vfs.file_id = mf.file_id
        ORDER BY (vfs.io_stall_read_ms + vfs.io_stall_write_ms) DESC;
      label: "sqlserver_file_io_latency"

    # 8) Transaction log usage
    - query: |
        SELECT
          DB_NAME(database_id) AS database_name,
          total_log_size_in_bytes/1024/1024 AS total_log_mb,
          used_log_space_in_bytes/1024/1024 AS used_log_mb,
          used_log_space_in_percent
        FROM sys.dm_db_log_space_usage;
      label: "sqlserver_log_usage"

    # 9) Index usage stats (top unused / over-used)
    - query: |
        SELECT TOP 50
          DB_NAME() AS database_name,
          OBJECT_SCHEMA_NAME(i.object_id) AS schema_name,
          OBJECT_NAME(i.object_id) AS table_name,
          i.name AS index_name,
          i.index_id,
          us.user_seeks, us.user_scans, us.user_lookups, us.user_updates
        FROM sys.indexes i
        LEFT JOIN sys.dm_db_index_usage_stats us
          ON us.object_id = i.object_id AND us.index_id = i.index_id AND us.database_id = DB_ID()
        WHERE i.is_hypothetical = 0 AND i.index_id > 0
        ORDER BY (ISNULL(us.user_updates,0) - (ISNULL(us.user_seeks,0)+ISNULL(us.user_scans,0)+ISNULL(us.user_lookups,0))) DESC;
      label: "sqlserver_index_usage"
```

> **Notes**
> - Set the password via environment variable `SQLSERVER_PWD` using **Metricbeat keystore**.  
> - You can add multiple `hosts:` entries for multiple instances.  
> - For DB‑scoped queries (like index usage), duplicate the query block per database (override `database=` in the connection string).

---

## ⚙️ Metricbeat — MSSQL module (optional, built‑ins)

If you prefer built-in collectors, enable `modules.d/mssql.yml`:

```yaml
- module: mssql
  metricsets: ["performance", "database", "transaction_log"]
  period: 30s
  hosts:
    - "sqlserver://elastic_ro:${SQLSERVER_PWD}@YOUR_SQL_HOST:1433?database=master&encrypt=true&trustservercertificate=true"
```

> Built-ins are great for general health; the **SQL module** above gives you **deep query insight**.

---

## 🪵 Winlogbeat — Deadlocks & Errors from Windows Event Log

Create `winlogbeat.yml` lines for Application log collection:

```yaml
winlogbeat.event_logs:
  - name: Application
    ignore_older: 72h
    event_id: 1205, 17883, 17884, 17887, 18456
    providers: [ "MSSQLSERVER", "SQLSERVERAGENT" ]
    tags: [ "sqlserver", "errors" ]
```

- **1205** → Deadlock victim (quick signal)  
- Add filters in Kibana or at input level (e.g., `processors.drop_event` for noise).

> **Tip:** Deadlock *graphs* are captured by the built‑in `system_health` Extended Events session. Consider a periodic SQL query to `sys.fn_xe_file_target_read_file` if you want to ingest full XML graphs (advanced).

---

## 🧪 Sanity SQL scripts (ad‑hoc, for testing)

**Top 10 running by elapsed:**

```sql
SELECT TOP 10
  r.session_id, r.status, r.command,
  r.cpu_time AS cpu_ms, r.total_elapsed_time AS elapsed_ms,
  r.reads, r.writes, r.blocking_session_id,
  DB_NAME(r.database_id) AS db_name,
  wt.wait_type, wt.wait_duration_ms,
  SUBSTRING(qt.text, (r.statement_start_offset/2)+1,
    ((CASE r.statement_end_offset WHEN -1 THEN DATALENGTH(qt.text) ELSE r.statement_end_offset END - r.statement_start_offset)/2)+1) AS statement_text,
  qt.text AS full_text
FROM sys.dm_exec_requests r
OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) qt
LEFT JOIN sys.dm_os_waiting_tasks wt ON r.session_id = wt.session_id
WHERE r.session_id <> @@SPID
ORDER BY r.total_elapsed_time DESC;
```

**Historical Top 10 by avg duration:**

```sql
SELECT TOP 10 
  CAST(qs.total_elapsed_time / NULLIF(qs.execution_count,0) AS BIGINT) AS avg_elapsed_ms,
  CAST(qs.total_worker_time / NULLIF(qs.execution_count,0) AS BIGINT) AS avg_cpu_ms,
  CAST(qs.total_logical_reads / NULLIF(qs.execution_count,0) AS BIGINT) AS avg_reads,
  CAST(qs.total_logical_writes / NULLIF(qs.execution_count,0) AS BIGINT) AS avg_writes,
  qs.execution_count, qs.last_execution_time,
  SUBSTRING(qt.text, (qs.statement_start_offset/2)+1,
    ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(qt.text) ELSE qs.statement_end_offset END - qs.statement_start_offset)/2)+1) AS statement_text,
  qt.text AS full_text
FROM sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) qt
ORDER BY avg_elapsed_ms DESC;
```

**Blocking chain overview:**

```sql
SELECT
  r.session_id, r.blocking_session_id, r.wait_type, r.wait_time AS wait_time_ms,
  DB_NAME(r.database_id) AS db_name,
  SUBSTRING(qt.text, (r.statement_start_offset/2)+1,
    ((CASE r.statement_end_offset WHEN -1 THEN DATALENGTH(qt.text) ELSE r.statement_end_offset END - r.statement_start_offset)/2)+1) AS statement_text
FROM sys.dm_exec_requests r
OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) qt
WHERE r.blocking_session_id <> 0
ORDER BY r.wait_time DESC;
```

**Wait stats (top):**

```sql
SELECT TOP 20
  wait_type, waiting_tasks_count, wait_time_ms, signal_wait_time_ms
FROM sys.dm_os_wait_stats
WHERE wait_type NOT LIKE 'SLEEP%'
ORDER BY wait_time_ms DESC;
```

**Log usage:**

```sql
SELECT
  DB_NAME(database_id) AS database_name,
  total_log_size_in_bytes/1024/1024 AS total_log_mb,
  used_log_space_in_bytes/1024/1024 AS used_log_mb,
  used_log_space_in_percent
FROM sys.dm_db_log_space_usage;
```

**File I/O latency:**

```sql
SELECT DB_NAME(vfs.database_id) AS database_name,
       mf.name AS file_name,
       vfs.io_stall_read_ms / NULLIF(vfs.num_of_reads,0)  AS avg_read_ms,
       vfs.io_stall_write_ms / NULLIF(vfs.num_of_writes,0) AS avg_write_ms,
       vfs.num_of_reads, vfs.num_of_writes
FROM sys.dm_io_virtual_file_stats(NULL, NULL) vfs
JOIN sys.master_files mf ON vfs.database_id = mf.database_id AND vfs.file_id = mf.file_id
ORDER BY (vfs.io_stall_read_ms + vfs.io_stall_write_ms) DESC;
```

**TempDB usage:**

```sql
SELECT
  DB_NAME(database_id) AS database_name,
  SUM((user_object_reserved_page_count + internal_object_reserved_page_count) * 8) AS tempdb_space_kb
FROM sys.dm_db_file_space_usage
GROUP BY database_id;
```

**Index usage (find potentially unused):**

```sql
SELECT TOP 50
  DB_NAME() AS database_name,
  OBJECT_SCHEMA_NAME(i.object_id) AS schema_name,
  OBJECT_NAME(i.object_id) AS table_name,
  i.name AS index_name,
  i.index_id,
  us.user_seeks, us.user_scans, us.user_lookups, us.user_updates
FROM sys.indexes i
LEFT JOIN sys.dm_db_index_usage_stats us
  ON us.object_id = i.object_id AND us.index_id = i.index_id AND us.database_id = DB_ID()
WHERE i.is_hypothetical = 0 AND i.index_id > 0
ORDER BY (ISNULL(us.user_updates,0) - (ISNULL(us.user_seeks,0)+ISNULL(us.user_scans,0)+ISNULL(us.user_lookups,0))) DESC;
```

---

## 📊 Kibana — Suggested Lens Panels

> Create a new **Dashboard** → add **Lens** visualizations using the fields from `windows.perfmon`, `sql.query`, and `winlogbeat` indexes.

1) **Throughput**  
   - *Metric*: `batches_per_sec` (last value)  
   - *Line chart*: `batches_per_sec` over time (10s interval)  
   - *Metric*: `tx_per_sec` (last value)

2) **Top 10 Longest Running (now)**  
   - *Table* from `sqlserver_top10_running`: columns = `elapsed_ms`, `cpu_ms`, `database_name`, `session_id`, `blocking_session_id`, `statement_text`

3) **Top 10 Historical by Avg Duration**  
   - *Table* from `sqlserver_top10_historical`: `avg_elapsed_ms`, `avg_cpu_ms`, `execution_count`, `last_execution_time`, `statement_text`

4) **Blocking Sessions**  
   - *Table* from `sqlserver_blocking`: `wait_time_ms`, `session_id`, `blocking_session_id`, `wait_type`, `database_name`, `statement_text`

5) **Deadlocks**  
   - *Metric*: sum of `deadlocks_per_sec`  
   - *Line chart*: `deadlocks_per_sec` over time  
   - *Event table*: `winlogbeat-*` filtered on `event.code:1205`

6) **Wait Types**  
   - *Bar chart*: `wait_type` vs `wait_time_ms` (Top 20) from `sqlserver_waits`

7) **I/O Latency**  
   - *Table*: `database_name`, `file_name`, `avg_read_ms`, `avg_write_ms` (from `sqlserver_file_io_latency`)

8) **Log Usage**  
   - *Table*: `database_name`, `used_log_mb`, `used_log_space_in_percent` (from `sqlserver_log_usage`)

9) **TempDB Usage**  
   - *Bar*: `database_name` vs `tempdb_space_kb`

10) **Index Usage (candidate to review)**  
   - *Table*: `schema_name`, `table_name`, `index_name`, `user_seeks`, `user_scans`, `user_lookups`, `user_updates`

---

## 🚨 Alerting (Kibana Rules)

- **Deadlock detected**: if `deadlocks_per_sec > 0` over the last 1m → notify (email/Slack).  
- **Blocking**: if any row in `sqlserver_blocking` has `wait_time_ms > 30000` (30s).  
- **Slow queries (now)**: if max `elapsed_ms` in `sqlserver_top10_running` > 60000.  
- **Historical regression**: if `avg_elapsed_ms` P95 in `sqlserver_top10_historical` increases by >30% vs last 7 days (use threshold on a moving average).  
- **Log usage high**: if `used_log_space_in_percent > 80%`.  
- **Page life expectancy**: if `page_life_expectancy < 300`.  

> Use **“Threshold”** or **“EQL/ES|QL”** rule types. Point each rule to the corresponding index pattern and field, set conditions, schedule (e.g., every 1m), and action connectors.

---

## 🧯 Deadlocks — Extended Events (optional, richer detail)

SQL Server’s **`system_health`** Extended Events session captures deadlock graphs. You can ingest summaries by querying periodically:

```sql
;WITH x AS (
  SELECT CONVERT(XML, event_data) AS xdata
  FROM sys.fn_xe_file_target_read_file('system_health*.xel', NULL, NULL, NULL)
  WHERE object_name = 'xml_deadlock_report'
)
SELECT
  x.xdata.value('(//process-list/process/@spid)[1]','int')    AS spid_a,
  x.xdata.value('(//resource-list/*[1]/@dbid)[1]','int')      AS dbid,
  x.xdata.value('(//victim-list/victimProcess/@id)[1]','varchar(50)') AS victim_id,
  x.xdata AS deadlock_graph_xml
FROM x;
```

You can:  
- Store **counts** and **key attributes** via the SQL module (label it `sqlserver_deadlock_xe`).  
- Keep full XML graphs out of Elastic (or store to **cold** indices) to avoid noise.

---

## 🧱 Windows Services & Deployment Tips

- Install Metricbeat/Winlogbeat as **Windows services**:  
  ```powershell
  .\metricbeat.exe install; Start-Service metricbeat
  .\winlogbeat.exe install;  Start-Service winlogbeat
  ```
- Put SQL credentials in **keystore**:  
  ```powershell
  .\metricbeat keystore create
  .\metricbeat keystore add SQLSERVER_PWD
  ```
- Validate configs: `metricbeat test config -e` and `test output -e`.  
- Check indices: `GET metricbeat-*/_search` (Dev Tools) and verify documents for each `label` above.

---

## 🛠 Troubleshooting Cheatsheet

- **No DMV access** → confirm `GRANT VIEW SERVER STATE`.  
- **PerfMon paths invalid** → open **perfmon.exe** and copy the exact counter names.  
- **No deadlocks 1205** → app may swallow errors; also rely on PerfMon counter, and/or XE.  
- **Plan cache flush** (restarts/recompiles) → historical top 10 will reset; combine with APM if needed.  
- **High CPU but low elapsed** → compute-bound workload; see **wait stats** (low waits) and **Top CPU** by query.  
- **High elapsed & high PAGEIOLATCH_*** → storage latency; check **file I/O latency** panel.

---

## ✅ Summary (what you get)

- **Throughput**: batches & tx/sec  
- **Top N slow queries** (live & historical) with **SQL text**  
- **Deadlocks**: real-time signals + optional graphs via XE  
- **Blocking chains**, **waits**, **TempDB**, **log usage**, **I/O latency**, **index usage**  
- **Kibana dashboards** + **Alerting** rules for SRE‑grade visibility

---

## 📎 Appendix: Optional APM (Deep Trace)

If you own the application code, Elastic **APM .NET** can add **end-to-end traces** (web → DB) and correlate with the above metrics. This is optional but powerful for root-cause.

---

**Author’s note:** Adjust query `period` to your workload. For busy systems, 10–15s is fine; for quieter servers 30–60s preserves overhead. DMV queries are lightweight but always validate in staging first.
