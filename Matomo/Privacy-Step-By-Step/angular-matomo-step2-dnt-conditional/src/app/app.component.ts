import { Component, AfterViewInit } from '@angular/core';
import { Router, NavigationEnd } from '@angular/router';
import { filter, distinctUntilChanged, map } from 'rxjs/operators';
import { MatomoService } from './services/matomo.service';

function currentPath(): string {
  // W kroku 3 zsanityzujemy (bez query/hash). W kroku 2 zostawiamy pełną ścieżkę.
  return window.location.pathname + window.location.search + window.location.hash;
}

@Component({
  selector: 'app-root',
  template: '<router-outlet></router-outlet>',
})
export class AppComponent implements AfterViewInit {
  private lastUrl = currentPath();
  private firstTracked = false;

  constructor(private readonly router: Router, private readonly matomo: MatomoService) {
    this.matomo.init();

    this.router.events
      .pipe(
        filter(e => e instanceof NavigationEnd),
        map(() => currentPath()),
        distinctUntilChanged()
      )
      .subscribe((path: string) => {
        const title = document.title;
        const ref = this.lastUrl;
        this.matomo.trackPageView(path, title, this.firstTracked ? ref : undefined);
        this.firstTracked = true;
        this.lastUrl = path;
      });
  }

  ngAfterViewInit(): void {
    if (!this.firstTracked) {
      const path = currentPath();
      this.matomo.trackPageView(path, document.title);
      this.firstTracked = true;
      this.lastUrl = path;
    }
  }
}
