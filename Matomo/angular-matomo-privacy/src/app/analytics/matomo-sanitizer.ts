import { environment } from '../../environments/environment';

const MATOMO_PHP = '/matomo.php';

function looksLikeMatomo(url: string): boolean {
  try {
    const u = new URL(url, window.location.origin);
    return u.origin === environment.MATOMO_BASE_URL && u.pathname.endsWith(MATOMO_PHP);
  } catch { return false; }
}

function stripParams(u: URL) {
  // Usuń User ID i wszystkie Custom Dimensions
  u.searchParams.delete('uid');
  for (let i = 1; i <= 999; i++) u.searchParams.delete(`dimension${i}`);

  // Usuń potencjalne PII w query (dopasuj do własnych realiów)
  ['email','e-mail','phone','tel','token','user','userid','session','auth','name'].forEach(k => u.searchParams.delete(k));
}

function sanitizeUrl(url: string | URL): string {
  const u = url instanceof URL ? url : new URL(url, window.location.origin);
  stripParams(u);
  return u.toString();
}

// fetch
function patchFetch() {
  if (!('fetch' in window)) return;
  const orig = window.fetch.bind(window);
  (window as any).fetch = (input: RequestInfo | URL, init?: RequestInit) => {
    try {
      let urlStr = (typeof input === 'string') ? input : (input instanceof URL ? input.toString() : (input as Request).url);
      if (looksLikeMatomo(urlStr)) {
        urlStr = sanitizeUrl(urlStr);
        if (typeof input === 'string' || input instanceof URL) {
          input = urlStr as any;
        } else {
          input = new Request(urlStr, input as RequestInit);
        }
        if (init?.body && typeof init.body === 'string' && init.headers && (init.headers as any)['Content-Type']?.includes('application/x-www-form-urlencoded')) {
          const p = new URLSearchParams(init.body as string);
          p.delete('uid');
          for (let i = 1; i <= 999; i++) p.delete(`dimension${i}`);
          init.body = p.toString();
        }
      }
    } catch {}
    return orig(input as any, init);
  };
}

// XHR
function patchXHR() {
  const XHR = (window as any).XMLHttpRequest;
  if (!XHR) return;
  const openOrig = XHR.prototype.open;
  const sendOrig = XHR.prototype.send;

  XHR.prototype.open = function(method: string, url: string) {
    (this as any).__isMatomo = looksLikeMatomo(url);
    (this as any).__matomoUrl = (this as any).__isMatomo ? sanitizeUrl(url) : url;
    return openOrig.apply(this, [method, (this as any).__matomoUrl]);
  };

  XHR.prototype.send = function(body?: Document | BodyInit | null) {
    if ((this as any).__isMatomo && typeof body === 'string') {
      const p = new URLSearchParams(body);
      p.delete('uid');
      for (let i = 1; i <= 999; i++) p.delete(`dimension${i}`);
      body = p.toString();
    }
    return sendOrig.apply(this, [body as any]);
  };
}

// sendBeacon
function patchBeacon() {
  if (!('sendBeacon' in navigator)) return;
  const orig = navigator.sendBeacon.bind(navigator);
  (navigator as any).sendBeacon = (url: string | URL, data?: any) => {
    try {
      if (looksLikeMatomo(url.toString())) {
        url = sanitizeUrl(url);
        if (typeof data === 'string') {
          const p = new URLSearchParams(data);
          p.delete('uid');
          for (let i = 1; i <= 999; i++) p.delete(`dimension${i}`);
          data = p.toString();
        }
      }
    } catch {}
    return orig(url as any, data);
  };
}

// Image().src (fallback piksel)
function patchImageSrc() {
  const proto = (window as any).HTMLImageElement?.prototype;
  if (!proto) return;
  const desc = Object.getOwnPropertyDescriptor(proto, 'src');
  if (!desc?.set || !desc?.get) return;
  Object.defineProperty(proto, 'src', {
    get: desc.get,
    set(value: string) {
      try {
        if (looksLikeMatomo(value)) {
          value = sanitizeUrl(value);
        }
      } catch {}
      return desc.set!.call(this, value);
    }
  });
}

export function installMatomoRequestSanitizer() {
  if (!environment.HARD_STRIP_UID_AND_DIMENSIONS) return;
  patchFetch();
  patchXHR();
  patchBeacon();
  patchImageSrc();
}
