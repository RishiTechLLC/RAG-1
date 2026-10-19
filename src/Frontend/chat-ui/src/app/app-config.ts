import { InjectionToken } from '@angular/core';

/** Runtime settings loaded from /config.json so one build can be deployed to any environment. */
export interface RuntimeConfig {
  /** Base URL of the Agents API. Empty = same origin (dev server proxies /api to the backend). */
  apiBaseUrl: string;
}

export const RUNTIME_CONFIG = new InjectionToken<RuntimeConfig>('RUNTIME_CONFIG');

export async function loadRuntimeConfig(): Promise<RuntimeConfig> {
  try {
    const response = await fetch('config.json', { cache: 'no-store' });
    if (response.ok) {
      const config = (await response.json()) as Partial<RuntimeConfig>;
      return { apiBaseUrl: (config.apiBaseUrl ?? '').replace(/\/+$/, '') };
    }
  } catch {
    // Fall through to defaults.
  }
  return { apiBaseUrl: '' };
}
