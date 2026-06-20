import { Configuration, LogLevel } from "@azure/msal-browser";

// Replace these values after creating your Microsoft Entra External ID tenant and app registrations.
export const authConfig = {
  tenantSubdomain: import.meta.env.VITE_TENANT_SUBDOMAIN ?? "YOUR_TENANT_SUBDOMAIN", // example: pfoh
  tenantId: import.meta.env.VITE_TENANT_ID ?? "YOUR_EXTERNAL_TENANT_ID",
  spaClientId: import.meta.env.VITE_SPA_CLIENT_ID ?? "YOUR_SPA_APP_CLIENT_ID",
  apiClientId: import.meta.env.VITE_API_CLIENT_ID ?? "YOUR_API_APP_CLIENT_ID"
};

export const apiScope = `api://${authConfig.apiClientId}/access_as_user`;

export const msalConfig: Configuration = {
  auth: {
    clientId: authConfig.spaClientId,
    authority: `https://${authConfig.tenantSubdomain}.ciamlogin.com/${authConfig.tenantId}`,
    redirectUri: window.location.origin,
    postLogoutRedirectUri: window.location.origin,
    knownAuthorities: [`${authConfig.tenantSubdomain}.ciamlogin.com`]
  },
  cache: {
    cacheLocation: "sessionStorage",
    storeAuthStateInCookie: false
  },
  system: {
    loggerOptions: {
      loggerCallback: (level, message, containsPii) => {
        if (!containsPii && level <= LogLevel.Warning) console.warn(message);
      },
      logLevel: LogLevel.Warning,
      piiLoggingEnabled: false
    }
  }
};

export const loginRequest = {
  scopes: [apiScope]
};
