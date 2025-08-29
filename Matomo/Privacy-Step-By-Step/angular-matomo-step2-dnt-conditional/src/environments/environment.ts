export const environment = {
  production: false,
  matomo: {
    enabled: true,
    respectDnt: false, // DEV: testuj telemetrię niezależnie od DNT
    url: 'https://twoj-matomo.example.com/',
    siteId: '1',
  },
};
