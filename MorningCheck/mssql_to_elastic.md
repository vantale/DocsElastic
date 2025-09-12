# Ingesting MS SQL Table Data into Elastic Stack and Visualising in Kibana

## 1) Preparation

### A. JDBC driver for MS SQL
1. Download `mssql-jdbc-<ver>.jar` (Microsoft JDBC Driver for SQL Server).  
2. Copy it to the Logstash server, e.g.:  
   `/usr/share/logstash/logstash-core/lib/jars/mssql-jdbc.jar`  
   *(or specify its path in `jdbc_driver_library`).*

### B. DB account
- Create a user with **SELECT rights** on that table (read-only).

### C. Assumptions
- The table has:
  - A **key column** (e.g. `my_key`).  
  - A **timestamp** (`created_at` and/or `updated_at`).  
- If `updated_at` is not available, use an ever-increasing key like `id`.

---

## 2) Logstash Pipeline (MS SQL → Elasticsearch)

Create: `/etc/logstash/conf.d/mssql_table.conf`

```conf
input {
  jdbc {
    jdbc_connection_string => "jdbc:sqlserver://<HOST>:1433;databaseName=<DB_NAME>;encrypt=true;trustServerCertificate=true"
    jdbc_user => "<DB_USER>"
    jdbc_password => "<DB_PASS>"
    jdbc_driver_class => "com.microsoft.sqlserver.jdbc.SQLServerDriver"
    jdbc_driver_library => "/usr/share/logstash/logstash-core/lib/jars/mssql-jdbc.jar"

    # Schedule: every minute
    schedule => "* * * * *"

    last_run_metadata_path => "/var/lib/logstash/.jdbc_last_run_mssql_table"

    jdbc_paging_enabled => true
    jdbc_page_size => 5000

    statement => "
      SELECT
        t.id,
        t.my_key,
        t.col1,
        t.col2,
        t.created_at,
        t.updated_at,
        CASE
          WHEN t.updated_at IS NULL OR CONVERT(datetime2, t.updated_at) <= CONVERT(datetime2, t.created_at)
          THEN 'new' ELSE 'updated'
        END AS row_status
      FROM dbo.MyTable t
      WHERE
        t.updated_at > :sql_last_value
        OR (:sql_last_value IS NULL)
      ORDER BY t.updated_at ASC
    "

    use_column_value => false
    tracking_column_type => "timestamp"
    clean_run => false
  }
}

filter {
  date {
    match => ["updated_at", "ISO8601", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.SSS"]
    target => "updated_at"
    tag_on_failure => []
  }
  date {
    match => ["created_at", "ISO8601", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.SSS"]
    target => "created_at"
    tag_on_failure => []
  }

  mutate {
    copy => { "updated_at" => "@timestamp" }
  }
  mutate {
    add_field => { "event.dataset" => "mssql.mytable" }
  }
}

output {
  elasticsearch {
    hosts => ["http://<ELASTIC_HOST>:9200"]
    index => "mssql-mytable-%{+yyyy.MM.dd}"
    document_id => "%{id}"
    action => "index"
  }
  stdout { codec => rubydebug }
}
```

---

## 3) Optional Index Template

Ensures correct mapping for fields:

```json
PUT _index_template/mssql_mytable_template
{
  "index_patterns": ["mssql-mytable-*"],
  "template": {
    "mappings": {
      "properties": {
        "my_key": { "type": "keyword" },
        "row_status": { "type": "keyword" },
        "created_at": { "type": "date" },
        "updated_at": { "type": "date" }
      }
    }
  }
}
```

---

## 4) Kibana – “New Rows by Key”

### A. Data view
- Create one for `mssql-mytable-*`.  
- Set **time field** to `@timestamp`.

### B. Discover
- KQL query:  
  ```
  row_status: "new"
  ```
- Add columns: `@timestamp`, `my_key`, `id`, `created_at`, `updated_at`.

### C. Lens visualisations
- **Filter**: `row_status: "new"`.  
- **Timeline chart**: Date histogram on `@timestamp`, breakdown by `my_key`.  
- **Table**: Count of new rows per `my_key`.

---

## 5) Common Pitfalls

- **No `updated_at`**: use `id` as `tracking_column`.  
- **Time zones**: ensure consistency (UTC vs local).  
- **Performance**: use paging, proper DB indexes, initial bulk import for large tables.  
- **Deduplication**: set `document_id => "%{id}"` for one document per row.  
- **First occurrence only**: use `action => "create"` instead of `index`.

---

👉 If you provide the actual table and column names (`id`, `my_key`, timestamps) and specify whether you want **every new/updated row** or only the **first occurrence per key**, the SQL and pipeline can be tailored precisely for your case.
