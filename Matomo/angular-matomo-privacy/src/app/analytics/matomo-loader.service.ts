import { Injectable } from '@angular/core';
import { environment } from '../../environments/environment';

declare global {
    interface Window {
        _paq: any[];
    }
}

@Injectable({ providedIn: 'root' })
export class MatomoLoaderService {
    private loaded = false;

    async init(): Promise<void> {
        if (!environment.MATOMO_ENABLED || this.loaded) return;

        // 1) Kolejka Matomo musi istnieć przed załadowaniem matomo.js
        window._paq = window._paq || [];

        // 2) Privacy-first BEFORE ANYTHING:
        // — całkowicie bez cookies (dostępne w starych wersjach trackera)
        window._paq.push(['disableCookies']);

        // — wyłączenie "browser feature detection" tylko jeśli metoda istnieje
        //   (Matomo >= 4.7 ma tę metodę; starsze wydania jej nie znają)
        window._paq.push([function () {
            const t = this as any;
            if (typeof t.disableBrowserFeatureDetection === 'function') {
                t.disableBrowserFeatureDetection();
            }
        }]);

        // — respektuj Do Not Track
        window._paq.push(['setDoNotTrack', true]);

        // — nie wysyłaj referrera (ogranicza ryzyko przeniesienia PII w URL)
        window._paq.push(['setReferrerUrl', '']);

        // 3) Podstawowa konfiguracja instancji
        window._paq.push(['setTrackerUrl', `${environment.MATOMO_BASE_URL}/matomo.php`]);
        window._paq.push(['setSiteId', environment.MATOMO_SITE_ID]);

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
