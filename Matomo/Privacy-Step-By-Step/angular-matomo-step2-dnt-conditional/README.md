# KROK 2 — Do Not Track (warunkowo wg przeglądarki)

**Co zawiera:**
- Cookieless (`disableCookies`).
- `respectDnt` sterowane przez environment:
  - DEV (`environment.ts`): `respectDnt: false` → telemetria zawsze (łatwiej testować).
  - PROD (`environment.prod.ts`): `respectDnt: true` → jeśli DNT=1, **nie ładujemy `matomo.js`** i nic nie wysyłamy.
- Dodatkowo `setDoNotTrack` informuje tracker (gdy `respectDnt` jest `true`).

**Test:**
1) Z `respectDnt: true` włącz DNT w przeglądarce. Po odświeżeniu:
   - brak `matomo.js` w Network,
   - brak requestów do `matomo.php`,
   - brak cookies `_pk_*`.
2) Wyłącz DNT (lub użyj DEV z `respectDnt: false`) → hity wracają.

**Następny krok:** sanitizacja URL (bez `?` i `#`) oraz dalej Web Vitals jako eventy bez PII.
