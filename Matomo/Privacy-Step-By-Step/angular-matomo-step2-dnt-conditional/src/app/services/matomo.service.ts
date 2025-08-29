import { Injectable, Inject, PLATFORM_ID, NgZone } from '@angular/core';
import { DOCUMENT, isPlatformBrowser } from '@angular/common';
import { environment } from '../../environments/environment';

declare global {
  interface Window { _paq?: any[]; }
}

@Injectable({ providedIn: 'root' })
export class MatomoService {
  private initialized = false;

  private get baseUrl(): string {
    const u = (environment.matomo?.url || '').trim();
    if (!u) return '';
    return u.endsWith('/') ? u : u + '/';
  }

  constructor(
    @Inject(DOCUMENT) private readonly document: Document,
    @Inject(PLATFORM_ID) private readonly platformId: Object,
    private readonly zone: NgZone,
  ) {}

  /** Inicjalizuje trackera — KROK 2: cookieless + DNT sterowany ustawieniami przeglądarki */
  init(): void {
    if (this.initialized || !isPlatformBrowser(this.platformId)) return;
    if (!environment.matomo?.enabled || !environment.matomo?.siteId || !this.baseUrl) return;

    // Guard: jeśli DNT=1 i respektujemy DNT, nie ładuj skryptu i nie trackuj
    if (environment.matomo?.respectDnt) {
      const dnt = (navigator as any).doNotTrack || (window as any).doNotTrack || (navigator as any).msDoNotTrack;
      if (dnt === '1' || dnt === 'yes') {
        console.info('[Matomo] DNT=1 → pomijam tracking i NIE ładuję skryptu.');
        this.initialized = true;
        return;
      }
    }

    this.zone.runOutsideAngular(() => {
      const w = window as Window;
      w._paq = w._paq || [];

      // Cookieless
      w._paq.push(['disableCookies']);
      // DNT sygnalizowane trackerowi (gdy respektujemy DNT)
      w._paq.push(['setDoNotTrack', !!environment.matomo?.respectDnt]);

      // Konfiguracja trackera
      w._paq.push(['setTrackerUrl', this.baseUrl + 'matomo.php']);
      w._paq.push(['setSiteId', environment.matomo.siteId]);
      w._paq.push(['enableLinkTracking']); // tylko raz

      // Wstrzyknięcie matomo.js jeżeli brak
      const src = this.baseUrl + 'matomo.js';
      const existing = this.document.querySelector(`script[src="${src}"]`) as HTMLScriptElement | null;
      if (!existing) {
        const g = this.document.createElement('script');
        g.async = true;
        g.src = src;
        const s = this.document.getElementsByTagName('script')[0];
        s?.parentNode?.insertBefore(g, s);
      }

      // Pomocniczy log
      w._paq.push([function () {
        // @ts-ignore Matomo tracker context
        console.debug('[Matomo] ready (cookieless + DNT conditional) | siteId=', this.getSiteId?.(), 'url=', this.getTrackerUrl?.());
      }]);
    });

    this.initialized = true;
  }

  /** Wysyła pageview dla SPA. */
  trackPageView(url?: string, title?: string, referrer?: string): void {
    if (!isPlatformBrowser(this.platformId)) return;
    if (!environment.matomo?.enabled) return;

    this.zone.runOutsideAngular(() => {
      const w = window as Window;
      w._paq = w._paq || [];

      if (referrer) w._paq.push(['setReferrerUrl', referrer]);
      if (url)      w._paq.push(['setCustomUrl', url]);
      if (title)    w._paq.push(['setDocumentTitle', title]);

      w._paq.push(['trackPageView']);
    });
  }

  /** Minimalne zdarzenia (opcjonalnie). */
  trackEvent(category: string, action: string, name?: string, value?: number): void {
    if (!isPlatformBrowser(this.platformId)) return;
    if (!environment.matomo?.enabled) return;

    this.zone.runOutsideAngular(() => {
      const w = window as Window;
      w._paq = w._paq || [];
      w._paq.push(['trackEvent', category, action, name, value]);
    });
  }
}
