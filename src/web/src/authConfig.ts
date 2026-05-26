import type { Configuration, PopupRequest } from "@azure/msal-browser";
import { config } from "./config";

const redirectUri = new URL("/auth.html", window.location.origin).toString();

export const msalConfig: Configuration = {
  auth: {
    clientId: config.msalClientId,
    authority: config.msalAuthority,
    redirectUri,
    postLogoutRedirectUri: redirectUri,
  },
  cache: {
    cacheLocation: "localStorage",
  },
};

export const loginRequest: PopupRequest = {
  scopes: ["https://ai.azure.com/.default"],
  redirectUri,
};
