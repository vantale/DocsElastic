import { Component, OnDestroy, OnInit } from '@angular/core';
import { Router, NavigationEnd } from '@angular/router';
import { filter, Subscription } from 'rxjs';
import { Title } from '@angular/platform-browser';
import { MatomoLoaderService } from './analytics/matomo-loader.service';
import { MatomoService } from './analytics/matomo.service';
import { PerfService } from './analytics/perf.service';
import { installMatomoRequestSanitizer } from './analytics/matomo-sanitizer';
import { environment } from '../environments/environment';

@Component({
  selector: 'app-root',
  template: '<router-outlet></router-outlet>'
})
export class AppComponent implements OnInit, OnDestroy {
  private sub?: Subscription;

  constructor(
    private router: Router,
    private title: Title,
    private loader: MatomoLoaderService,
    private matomo: MatomoService,
    private perf: PerfService
  ) {}

  async ngOnInit() {
    if (!environment.MATOMO_ENABLED) return;

    // 1) Twarde bezpieczniki: wyczyść uid/dimensions z każdego requestu do Matomo
    installMatomoRequestSanitizer();

    // 2) Załaduj Matomo z ustawieniami PRIVACY
    await this.loader.init();

    // 3) Track pierwszego widoku
    this.matomo.trackPageView(window.location.href, document.title);

    // 4) SPA: track na każdą zmianę trasy
    this.sub = this.router.events
      .pipe(filter(e => e instanceof NavigationEnd))
      .subscribe(() => {
        this.matomo.trackPageView(window.location.href, document.title);
      });

    // 5) Metryki wydajności (anonimowe eventy)
    this.perf.init();
  }

  ngOnDestroy() {
    this.sub?.unsubscribe();
  }
}
