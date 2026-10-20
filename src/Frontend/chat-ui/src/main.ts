import { bootstrapApplication } from '@angular/platform-browser';
import { createAppConfig } from './app/app.config';
import { loadRuntimeConfig } from './app/app-config';
import { App } from './app/app';

loadRuntimeConfig()
  .then((runtime) => bootstrapApplication(App, createAppConfig(runtime)))
  .catch((err) => console.error(err));
