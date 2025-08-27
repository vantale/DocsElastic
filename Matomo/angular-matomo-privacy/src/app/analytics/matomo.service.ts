import { Injectable } from '@angular/core';
import { environment } from '../../environments/environment';

@Injectable({ providedIn: 'root' })
export class MatomoService {
  private get paq(): any[] {
    (window as any)._paq = (window as any)._paq || [];
    return (window as any)._paq;
  }

  /** Sanitizes URL to avoid leaking PII from query/hash */
  private sanitizeUrl(url: string): string {
    if (!environment.SANITIZE_PAGE_URL) return url;
    try {
      const u = new URL(url, window.location.origin);
      u.search = '';
      u.hash = '';
      return u.pathname; // tylko ścieżka
    } catch { return url; }
  }

  trackPageView(url: string, title?: string) {
    const clean = this.sanitizeUrl(url);
    this.paq.push(['setCustomUrl', clean]);
    if (title) this.paq.push(['setDocumentTitle', title]);
    this.paq.push(['trackPageView']);
  }

  // Minimalne, anonimowe eventy wydajności (category: 'perf')
  trackPerf(name: 'TTFB'|'FCP'|'LCP'|'CLS', valueMsOrScore: number) {
    let val = valueMsOrScore;
    if (name === 'CLS') val = Math.round(valueMsOrScore * 1000);
    else val = Math.round(valueMsOrScore);
    this.paq.push(['trackEvent', 'perf', name, '', val]);
  }
}
