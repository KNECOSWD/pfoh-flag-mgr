import { Configuration, LogLevel } from "@azure/msal-browser";
import {
  claimMatchesExtension as matchExtensionClaim,
  defaultExtensionsAppId,
  normalizeExtensionsAppId as normalizeExtensionsAppIdValue,
  readMappedAccountClaim as readMappedClaim
} from "./entraClaims.js";

export { defaultExtensionsAppId };

// Replace these values after creating your Microsoft Entra External ID tenant and app registrations.
export const authConfig = {
  tenantSubdomain: import.meta.env.VITE_TENANT_SUBDOMAIN ?? "YOUR_TENANT_SUBDOMAIN", // example: pfoh
  tenantId: import.meta.env.VITE_TENANT_ID ?? "YOUR_EXTERNAL_TENANT_ID",
  spaClientId: import.meta.env.VITE_SPA_CLIENT_ID ?? "YOUR_SPA_APP_CLIENT_ID",
  apiClientId: import.meta.env.VITE_API_CLIENT_ID ?? "YOUR_API_APP_CLIENT_ID",
  // b2c-extensions-app application id. A non-empty env value replaces the live default.
  extensionsAppId: import.meta.env.VITE_ENTRA_EXTENSIONS_APP_ID || defaultExtensionsAppId
};

export function normalizeExtensionsAppId(value?: string | null) {
  return normalizeExtensionsAppIdValue(value);
}

export function claimMatchesExtension(
  claimType: string,
  attribute: "Phone" | "Organization",
  extensionsAppId: string | null | undefined = authConfig.extensionsAppId
) {
  return matchExtensionClaim(claimType, attribute, extensionsAppId);
}

export function readMappedAccountClaim(
  claims: Record<string, unknown> | null | undefined,
  attribute: "Phone" | "Organization",
  extensionsAppId: string | null | undefined = authConfig.extensionsAppId
) {
  return readMappedClaim(claims, attribute, extensionsAppId) as string;
}

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
