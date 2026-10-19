# chat-ui

Angular 22 chat front end for the multi-agent API (`src/Agents/Agents.Api`).

```bash
npm install
npm start          # http://localhost:4200, /api is proxied to Agents.Api (proxy.conf.js)
npm run test:ci    # Vitest unit tests
npm run build      # production build -> dist/chat-ui/browser
```

* The API base URL is read at runtime from `public/config.json` (`apiBaseUrl`). It is empty locally, which means same-origin through the dev proxy. The deployment workflow writes the Azure URL into this file.
* `public/staticwebapp.config.json` configures Azure Static Web Apps (SPA fallback, headers).
* Under Aspire, the dev server is started by the AppHost and `AGENTS_API_URL` is injected automatically.
