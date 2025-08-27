import { Injectable } from '@angular/core';
import { MatomoService } from './matomo.service';

@Injectable({ providedIn: 'root' })
export class PerfService {
  private cls = 0;
  private lcp = 0;
  private fcp = 0;

  constructor(private matomo: MatomoService) {}

  init() {
    // TTFB (Navigation Timing)
    const nav = performance.getEntriesByType('navigation')[0] as PerformanceNavigationTiming | undefined;
    if (nav) {
      const ttfb = (nav as any).responseStart ?? 0; // ms
      if (ttfb >= 0) this.matomo.trackPerf('TTFB', ttfb);
    }

    // FCP
    try {
      const obsFCP = new PerformanceObserver((list) => {
        for (const e of list.getEntries()) {
          if ((e as any).name === 'first-contentful-paint') {
            this.fcp = e.startTime;
            this.matomo.trackPerf('FCP', this.fcp);
            obsFCP.disconnect();
            break;
          }
        }
      });
      (obsFCP as any).observe({ type: 'paint', buffered: true });
    } catch {}

    // LCP
    try {
      const obsLCP = new PerformanceObserver((list) => {
        const entries = list.getEntries();
        const last = entries[entries.length - 1];
        if (last) {
          this.lcp = last.startTime;
        }
      });
      (obsLCP as any).observe({ type: 'largest-contentful-paint', buffered: true });
      addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'hidden' && this.lcp > 0) {
          this.matomo.trackPerf('LCP', this.lcp);
        }
      });
    } catch {}

    // CLS (cumulative)
    try {
      const obsCLS = new PerformanceObserver((list) => {
        for (const e of list.getEntries() as any) {
          if (!e.hadRecentInput) this.cls += e.value;
        }
      });
      (obsCLS as any).observe({ type: 'layout-shift', buffered: true });
      addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'hidden' && this.cls > 0) {
          this.matomo.trackPerf('CLS', this.cls);
        }
      });
    } catch {}
  }
}
