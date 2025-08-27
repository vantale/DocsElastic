# Angular 15 + Matomo (privacy-first, bez zgody)

Ten pakiet uruchamia **minimalny, zgodny z RODO** tracking Matomo:
- **brak cookies**, **brak fingerprintingu**, **brak User ID**,
- twarda **sanityzacja** każdego requestu do `matomo.php` (usuwa `uid`, `dimension*`, typowe PII w query/body),
- **zero** heatmap/session/media/form/link tracking,
- jedynie **anonimowe metryki wydajności** (TTFB/FCP/LCP/CLS) jako eventy.

## Pliki
- `src/environments/environment.ts` — konfiguracja (ustaw `MATOMO_BASE_URL`, `MATOMO_SITE_ID`).
- `src/app/analytics/matomo-loader.service.ts` — ładuje `matomo.js` z privacy-hardening.
- `src/app/analytics/matomo-sanitizer.ts` — patchuje fetch/XHR/beacon/IMG, usuwa `uid` i `dimension*`.
- `src/app/analytics/matomo.service.ts` — `trackPageView`, eventy wydajności.
- `src/app/analytics/perf.service.ts` — zbiera TTFB/FCP/LCP/CLS.
- `src/app/analytics/matomo.types.d.ts` — typy dla kolejki `_paq`.
- `src/app/app.component.ts` — integracja SPA (pageview na route change + perf).

## Użycie
1. Skopiuj katalog `src/` do projektu Angular 15.
2. Ustaw w `environment.ts`:
   ```ts
   MATOMO_BASE_URL: 'https://analytics.twoja-domena.pl',
   MATOMO_SITE_ID: '1'
   ```
3. Upewnij się, że `AppComponent` jest Twoim rootem (lub przenieś integrację do innego miejsca wykonywanego po starcie).

## Uwaga
- Jeśli masz Matomo Tag Manager, możesz zamiast `matomo.js` wczytywać kontener MTM — ten pakiet celowo używa direct trackera dla pełnej kontroli.
- W razie potrzeby dodaj listę własnych kluczy PII do usuwania w `matomo-sanitizer.ts`.

