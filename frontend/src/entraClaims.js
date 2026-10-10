export const defaultExtensionsAppId = "a29a0dc7-4c9b-4cef-9f78-3444e62907d3";

const standardPhoneNames = new Set([
  "phone_number",
  "phonenumber",
  "mobile_phone",
  "mobilephone",
  "telephonenumber",
  "signinames.phonenumber",
  "strongauthenticationphonenumber"
]);

export function normalizeExtensionsAppId(value) {
  const compact = String(value ?? "").replace(/[{}\s-]/g, "").toLowerCase();
  return /^[0-9a-f]{32}$/.test(compact) ? compact : null;
}

/**
 * @param {string} claimType
 * @param {"Phone" | "Organization"} attribute
 * @param {string | null | undefined} [extensionsAppId]
 */
export function claimMatchesExtension(claimType, attribute, extensionsAppId = defaultExtensionsAppId) {
  const name = claimLocalName(claimType);
  const match = /^extension_([0-9a-f]{32})_(phone|organization)$/i.exec(name);
  if (!match || match[2].toLowerCase() !== String(attribute).toLowerCase()) {
    return false;
  }

  const configured = normalizeExtensionsAppId(extensionsAppId);
  const configuredWasSet = String(extensionsAppId ?? "").trim().length > 0;
  if (configuredWasSet && !configured) {
    return false;
  }

  return !configured || match[1].toLowerCase() === configured;
}

/**
 * @param {Record<string, unknown> | null | undefined} claims
 * @param {"Phone" | "Organization"} attribute
 * @param {string | null | undefined} [extensionsAppId]
 * @returns {string}
 */
export function readMappedAccountClaim(claims, attribute, extensionsAppId = defaultExtensionsAppId) {
  if (!claims) return "";

  const shortName = attribute === "Phone" ? "phone" : "organization";
  const keys = Object.keys(claims);
  const shortValue = firstStringValue(
    claims,
    keys.filter((key) => claimLocalName(key).toLowerCase() === shortName)
  );
  if (shortValue) return shortValue;

  const extensionValue = firstStringValue(
    claims,
    keys.filter((key) => claimMatchesExtension(key, attribute, extensionsAppId))
  );
  if (extensionValue) return extensionValue;

  if (attribute !== "Phone") return "";

  return firstStringValue(
    claims,
    keys.filter((key) => standardPhoneNames.has(claimLocalName(key).toLowerCase()))
  );
}

function claimLocalName(claimType) {
  const slash = String(claimType).lastIndexOf("/");
  return slash >= 0 ? String(claimType).slice(slash + 1) : String(claimType);
}

function firstStringValue(claims, keys) {
  for (const key of keys) {
    const value = claims[key];
    if (typeof value === "string" && value.trim()) {
      return value.trim();
    }

    if (Array.isArray(value)) {
      const first = value.find((item) => typeof item === "string" && item.trim());
      if (typeof first === "string") {
        return first.trim();
      }
    }
  }

  return "";
}
