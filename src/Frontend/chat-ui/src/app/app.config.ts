import { provideHttpClient, withFetch } from '@angular/common/http';
import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { RUNTIME_CONFIG, RuntimeConfig } from './app-config';

export function createAppConfig(runtime: RuntimeConfig): ApplicationConfig {
  return {
    providers: [
      provideBrowserGlobalErrorListeners(),
      provideHttpClient(withFetch()),
      { provide: RUNTIME_CONFIG, useValue: runtime },
    ],
  };
}
