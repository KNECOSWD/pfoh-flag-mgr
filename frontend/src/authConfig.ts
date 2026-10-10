import { Configuration, LogLevel } from "@azure/msal-browser";

// Replace these values after creating your Microsoft Entra External ID tenant and app registrations.
export const authConfig = {
  tenantSubdomain: import.meta.env.VITE_TENANT_SUBDOMAIN ?? "YOUR_TENANT_SUBDOMAIN", // example: pfoh
  tenantId: import.meta.env.VITE_TENANT_ID ?? "YOUR_EXTERNAL_TENANT_ID",
  spaClientId: import.meta.env.VITE_SPA_CLIENT_ID ?? "YOUR_SPA_APP_CLIENT_ID",
  apiClientId: import.meta.env.VITE_API_CLIENT_ID ?? "YOUR_API_APP_CLIENT_ID",
  // b2c-extensions-app application id. Empty until it is set. Not hard-coded.
  extensionsAppId: import.meta.env.VITE_ENTRA_EXTENSIONS_APP_ID ?? ""
};

export function normalizeExtensionsAppId(value?: string | null) {
  const compact = (value ?? "").replace(/[{}\s-]/g, "").toLowerCase();
  return /^[0-9a-f]{32}$/.test(compact) ? compact : null;
}

export function claimMatchesExtension(
  claimType: string,
  attribute: "Phone" | "Organization",
  extensionsAppId: string | null | undefined = authConfig.extensionsAppId
) {
  const slash = claimType.lastIndexOf("/");
  const name = slash >= 0 ? claimType.slice(slash + 1) : claimType;
  const match = /^extension_([0-9a-f]{32})_(phone|organization)$/i.exec(name);
  if (!match || match[2].toLowerCase() !== attribute.toLowerCase()) {
    return false;
  }

  const configured = normalizeExtensionsAppId(extensionsAppId);
  const configuredWasSet = (extensionsAppId ?? "").trim().length > 0;
  if (configuredWasSet && !configured) {
    return false;
  }

  return !configured || match[1].toLowerCase() === configured;
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
