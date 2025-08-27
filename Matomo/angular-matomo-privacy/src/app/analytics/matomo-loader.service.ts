import { Injectable } from '@angular/core';
import { environment } from '../../environments/environment';

@Injectable({ providedIn: 'root' })
export class MatomoLoaderService {
  private loaded = false;

  async init(): Promise<void> {
    if (!environment.MATOMO_ENABLED || this.loaded) return;

    // 1) Kolejka Matomo
    (window as any)._paq = (window as any)._paq || [];

    // 2) Ustawienia PRIVACY BEFORE ANYTHING:
    // — całkowicie bez cookies
    (window as any)._paq.push(['disableCookies']);
    // — nie korzystaj z fingerprintingu / cech przeglądarki
    (window as any)._paq.push(['disableBrowserFeatureDetection']);
    // — respektuj DNT (bez profilowania, jeszcze mocniej prywatnie)
    (window as any)._paq.push(['setDoNotTrack', true]);
    // — nie wysyłaj referrera (aby nie przeszły PII z poprzedniej strony)
    (window as any)._paq.push(['setReferrerUrl', '']);

    // 3) Konfiguracja instancji
    (window as any)._paq.push(['setTrackerUrl', `${environment.MATOMO_BASE_URL}/matomo.php`]);
    (window as any)._paq.push(['setSiteId', environment.MATOMO_SITE_ID]);

    // 4) NIE włączamy nic ponad podstawę (brak enableLinkTracking, form/media/heatmap)

    // 5) Załaduj bibliotekę matomo.js
    await this.loadScript(`${environment.MATOMO_BASE_URL}/matomo.js`);
    this.loaded = true;
  }

  private loadScript(src: string): Promise<void> {
    return new Promise((resolve, reject) => {
      const s = document.createElement('script');
      s.async = true;
      s.src = src;
      s.onload = () => resolve();
      s.onerror = (e) => reject(e);
      document.head.appendChild(s);
    });
  }
}
