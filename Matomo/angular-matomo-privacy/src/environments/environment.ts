export const environment = {
  production: false,

  // Minimalny, bezpieczny tracking – WŁĄCZONY
  MATOMO_ENABLED: true,

  // Twoje endpointy:
  MATOMO_BASE_URL: 'https://analytics.example.com', // bez trailing slash
  MATOMO_SITE_ID: '1',

  // Twarde usuwanie identyfikatorów i potencjalnych PII z requestów
  HARD_STRIP_UID_AND_DIMENSIONS: true,

  // Czy usuwać query/hash z URL strony (ochrona przed PII w adresie)
  SANITIZE_PAGE_URL: true
};
