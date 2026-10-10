import assert from "node:assert/strict";
import test from "node:test";
import { defaultExtensionsAppId, readMappedAccountClaim } from "../src/entraClaims.js";

const extensionPhone = "extension_a29a0dc74c9b4cef9f783444e62907d3_Phone";
const extensionOrganization = "extension_a29a0dc74c9b4cef9f783444e62907d3_Organization";

test("short phone and organization win over extension claims", () => {
  const claims = {
    [extensionPhone]: "+19995550100",
    [extensionOrganization]: "Extension Org",
    phone_number: "+18885550100",
    phone: " +12145551234 ",
    organization: " Plano Fire "
  };

  assert.equal(defaultExtensionsAppId, "a29a0dc7-4c9b-4cef-9f78-3444e62907d3");
  assert.equal(readMappedAccountClaim(claims, "Phone"), "+12145551234");
  assert.equal(readMappedAccountClaim(claims, "Organization"), "Plano Fire");
});

test("extension claims are used when the short names are absent", () => {
  const claims = {
    phone_number: "+19995550100",
    [extensionPhone]: "+12145551234",
    [extensionOrganization]: " Honor Guard ",
    extension_0123456789abcdef0123456789abcdef_Phone: "+18885550100",
    extension_0123456789abcdef0123456789abcdef_Organization: "Other"
  };

  assert.equal(readMappedAccountClaim(claims, "Phone"), "+12145551234");
  assert.equal(readMappedAccountClaim(claims, "Organization"), "Honor Guard");
});

test("a standard phone claim is the last fallback", () => {
  assert.equal(readMappedAccountClaim({ phone_number: " +19725550100 " }, "Phone"), "+19725550100");
  assert.equal(readMappedAccountClaim({}, "Organization"), "");
});
