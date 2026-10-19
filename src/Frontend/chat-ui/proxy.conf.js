// Dev-server proxy: forwards /api to the Agents API so the browser never needs CORS locally.
// Under Aspire the URL is injected (AGENTS_API_URL / services__agents-api__http__0);
// otherwise it falls back to the Agents.Api launch profile port.
const target =
  process.env['AGENTS_API_URL'] ||
  process.env['services__agents-api__https__0'] ||
  process.env['services__agents-api__http__0'] ||
  'http://localhost:5401';

module.exports = {
  '/api': { target, secure: false, changeOrigin: true, logLevel: 'info' },
};
