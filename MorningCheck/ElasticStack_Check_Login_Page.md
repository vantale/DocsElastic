
# Elastic Stack — Checking if a FE Login Page is Displayed Correctly (Uptime + UI Validation)

**Goal:** Verify that your website is operational and that the **login page actually renders correctly** (not just returns HTTP 200). Below are two complementary approaches:

- **Option A — Heartbeat (HTTP “lightweight” monitor):** Fast uptime check, status code, latency, and **expected text/snippet** in the raw HTML.  
- **Option B — Elastic Synthetics (Playwright “browser” monitor):** Real browser rendering (JS/SPAs), **assert UI elements**, take **screenshots/filmstrips**, capture waterfall, and alert on failures — **without Elastic Agent**.

> Works on Windows (ZIP installs), Linux, macOS, air‑gapped or on‑prem. You only need network reachability from the runner to your FE URL and to your Elasticsearch/Kibana (for reporting).

---

## Option A — Heartbeat (HTTP) for basic availability + “looks like login” heuristics

Use Heartbeat to ping your login URL and assert status + body contains an identifying fragment (e.g., page title, a label, or a stable `data-testid`).

### 1) Install Heartbeat (Windows ZIP)
1. Download Heartbeat ZIP matching your ES/Kibana version.  
2. Unzip to e.g. `C:\Elastic\heartbeat`.
3. In PowerShell (as admin):
   ```powershell
   cd C:\Elastic\heartbeat
   copy .\heartbeat.yml .\heartbeat.yml.bak
   ```

### 2) Configure `heartbeat.yml`
Replace placeholders: `ES_HOST`, `ES_PORT`, `ES_API_KEY` or `ES_USERNAME/ES_PASSWORD`, `LOGIN_URL`, and expected text.

```yaml
heartbeat.monitors:
  - type: http
    id: fe-login-http
    name: FE Login – HTTP check
    schedule: '@every 30s'
    enabled: true
    urls: ["https://LOGIN_URL.example.com/login"]
    # Expect success status codes (200–399)
    check.request:
      method: GET
      headers:
        User-Agent: "hb-fe-login/1.0"
    check.response:
      status: 200
      # One or more identifying fragments in the HTML
      body:
        - "Sign in"
        - "<title>Login</title>"
        - "data-testid=\"login-form\""  # escape quotes for YAML

# Output to Elasticsearch
output.elasticsearch:
  hosts: ["https://ES_HOST:ES_PORT"]
  api_key: "ES_API_KEY"   # or:
  # username: "elastic"
  # password: "your_password"
  ssl:
    verification_mode: full   # or 'certificate' for custom CA
    # certificate_authorities: ["C:/Elastic/certs/ca.crt"]

# (Optional) Tag with env/service
processors:
  - add_fields:
      target: ''
      fields:
        env: prod
        service: fe-login
```

> **Tip:** Use a **stable text** (not A/B, not i18n‑variant). If nothing is stable in raw HTML (SPA), switch to Option B (browser).

### 3) Run as a Windows service
```powershell
.\install-service-heartbeat.ps1
Start-Service heartbeat
# Logs (for quick debug):
.\heartbeat.exe -e -c .\heartbeat.yml
```

### 4) Visualize & alert in Kibana
- **Uptime app** → see monitor status, latency, failures.  
- Create **alert**: “When monitor status is Down for 2 checks → Slack/Email/Teams”.  
- Add a **Lens** panel: count of `monitor.status: "down"` by `service` over last 24h.


---

## Option B — Elastic Synthetics (Playwright) for real UI validation (no Agent)

This uses the **`@elastic/synthetics`** runner (Playwright) to render the login page, **assert that inputs/buttons are visible & enabled**, and ship results (and screenshots) to Kibana Synthetics. **No Elastic Agent required.**

### 1) Prereqs
- Node.js 18+ on the runner (Windows OK).  
- Kibana & Elasticsearch reachable from the runner.  
- An **API Key** with `synthetics write` (or use `kibana_url` + `kibana_username/password` — API key recommended).

### 2) Project scaffold
```powershell
mkdir C:\synthetics-fe-login
cd C:\synthetics-fe-login
npm init -y
npm i -D @elastic/synthetics
```

Create **`journeys/login.journey.ts`**:
```ts
import { journey, step, expect } from '@elastic/synthetics';

const LOGIN_URL = process.env.LOGIN_URL ?? 'https://LOGIN_URL.example.com/login';

journey('FE login page renders', ({ page, params }) => {
  step('open login', async () => {
    await page.goto(LOGIN_URL, { waitUntil: 'networkidle' });
  });

  step('fields and CTA are visible/enabled', async () => {
    // Adjust selectors to your DOM (data-testid recommended)
    await expect(page.locator('[data-testid="login-form"]')).toBeVisible();
    await expect(page.locator('input[type="email"], input[name="email"]')).toBeVisible();
    await expect(page.locator('input[type="password"], input[name="password"]')).toBeVisible();
    const button = page.getByRole('button', { name: /sign in|log in/i });
    await expect(button).toBeEnabled();
  });

  step('optional: visual snapshot', async () => {
    await page.screenshot({ path: 'artifacts/login.png', fullPage: false });
  });
});
```

Create **`synthetics.config.ts`** (report straight to Kibana Synthetics):
```ts
import type { SyntheticsConfig } from '@elastic/synthetics';

export default {
  params: {
    url: process.env.LOGIN_URL
  },
  playwrightOptions: {
    viewport: { width: 1440, height: 900 }
  },
  schedule: 5, // run every 5 minutes
  screenshot: 'on',
  ignoreHTTPSErrors: false,
  throttling: 'cable',
  output: 'elastic',
  elastic: {
    // Prefer API key for least-privilege auth
    apiKey: process.env.ES_API_KEY,
    kibanaUrl: process.env.KIBANA_URL
    // Alternatively:
    // username: process.env.KIBANA_USERNAME,
    // password: process.env.KIBANA_PASSWORD,
  }
} satisfies SyntheticsConfig;
```

### 3) Run locally (ad‑hoc) and/or schedule
Ad‑hoc validation:
```powershell
$env:LOGIN_URL="https://LOGIN_URL.example.com/login"
$env:KIBANA_URL="https://kibana.example.com:5601"
$env:ES_API_KEY="YOUR_API_KEY"
npx @elastic/synthetics .
```

As a scheduled task (Windows Task Scheduler):
- Program/script: `C:\Program Files\nodejs\npx.cmd`
- Arguments: `@elastic/synthetics C:\synthetics-fe-login`
- Repeat every 5 minutes.

### 4) Kibana — Synthetics app
- See **monitor**, last runs, **screenshots/filmstrips**, **waterfall**, and step timings.  
- Alert when **any step fails** or total duration exceeds a threshold.  
- Add panels to dashboards (pass/fail over time, core web vitals if you expand the journey).

> **Why “browser” monitors?** They catch SPA/JS failures, missing CSS/assets, CORS issues, or disabled backends that still return 200 but render an error shell.


---

## Choosing A vs B (or both)

| Need | Option A (Heartbeat HTTP) | Option B (Synthetics Browser) |
|---|---|---|---|
| Is the site up (200/latency)? | ✅ | ✅ |
| Verify **real** rendering of the login form | ⚠️ (heuristic) | ✅ |
| SPA/JS errors, missing assets, CSP/CORS issues | ❌ | ✅ |
| Screenshots & filmstrips | ❌ | ✅ |
| Easiest to run in tiny VM | ✅ | ⚠️ (needs Node/Playwright) |

**Recommended:** Run both. Use A for fast, cheap global uptime. Use B for **true UX correctness** of the login page.


---

## Extras & Hardening

- **Selectors:** Prefer stable `data-testid` attributes over text to avoid i18n/A/B flakiness.  
- **Auth gates:** If your login page sometimes redirects (SSO), pin the journey to the pre‑auth URL and check the **IdP banner/fields**.  
- **TLS checks:** In Heartbeat, set `ssl.certificate_authorities` and alert on **certificate expiry < N days** (separate TLS monitor).  
- **Network constraints:** If Kibana/ES are not reachable from the runner, buffer results to NDJSON and forward via Logstash.  
- **CI Integration:** Add `npx @elastic/synthetics .` as a smoke test in your build pipeline and fail builds on regressions.


---

## Ready‑to‑paste Alert (Kibana → Stack Rules → Uptime / Synthetics)

**Condition:** Monitor status is **Down** OR (Synthetics) **Step failed**.  
**Throttle:** 5 minutes.  
**Action message:**  
> `[{{rule.name}}] {{context.monitorName}} is {{context.status}} at {{context.checkedAt}}\nURL: {{context.url}}\nError: {{context.errorMessage}}`

---

## Troubleshooting

- **HTTP passes but Browser fails:** Likely JS runtime error, blocked asset, or timing. Inspect **Synthetics → Waterfall** and **console logs**; add `page.waitForSelector(...)`.  
- **Flaky steps:** Add **networkidle** waits, increase timeouts, or use **`data-testid`** selectors.  
- **Cert errors (on prem):** Import CA and set `ssl.certificate_authorities`.  
- **403 / 401:** Ensure the synthetic runner’s IP is allowed by WAF / IP allowlists.

---

### Minimal Files Recap

```
C:\Elastic\heartbeat\heartbeat.yml     # Option A
C:\synthetics-fe-login\journeys\login.journey.ts
C:\synthetics-fe-login\synthetics.config.ts
```

That’s it — you now have **uptime** and **UI‑level** assurance that the FE login page is truly working.
