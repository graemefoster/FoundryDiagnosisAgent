/**
 * Runtime configuration injected by the CLI server via window.__SENTINEL_CONFIG__.
 * Falls back to Vite env vars for local development.
 */

interface SentinelConfig {
  agentBaseUrl: string;
  msalClientId: string;
  msalAuthority: string;
  foundryAccessToken?: string;
  agentFilesBaseUrl?: string;
  agentFilesApiVersion?: string;
}

declare global {
  interface Window {
    __SENTINEL_CONFIG__?: Partial<SentinelConfig>;
  }
  // eslint-disable-next-line no-var
  var __APP_VERSION__: string;
}

function getConfig(): SentinelConfig {
  const runtime = window.__SENTINEL_CONFIG__ ?? {};
  return {
    agentBaseUrl: runtime.agentBaseUrl ?? import.meta.env.VITE_AGENT_BASE_URL ?? "",
    msalClientId: runtime.msalClientId ?? import.meta.env.VITE_MSAL_CLIENT_ID ?? "",
    msalAuthority: runtime.msalAuthority ?? import.meta.env.VITE_MSAL_AUTHORITY ?? "",
    foundryAccessToken: runtime.foundryAccessToken ?? import.meta.env.VITE_FOUNDRY_ACCESS_TOKEN ?? undefined,
    agentFilesBaseUrl: runtime.agentFilesBaseUrl ?? import.meta.env.VITE_AGENT_FILES_BASE_URL ?? undefined,
    agentFilesApiVersion: runtime.agentFilesApiVersion ?? import.meta.env.VITE_AGENT_FILES_API_VERSION ?? undefined,
  };
}

export const config = getConfig();
export const APP_VERSION: string = __APP_VERSION__;
