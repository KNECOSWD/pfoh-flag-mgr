import assert from "node:assert/strict";
import test from "node:test";
import { displayUsPhone, formatUsPhone, normalizeUsPhone, usPhoneErrorMessage } from "../src/usPhone.js";

const stored = "+12145551234";
const displayed = "(214) 555-1234";

test("accepts common US phone formats and stores +1 plus 10 digits", () => {
  for (const input of [
    "2145551234",
    "214-555-1234",
    "(214) 555-1234",
    "214.555.1234",
    "+1 214 555 1234",
    "1-214-555-1234",
    "+12145551234"
  ]) {
    assert.equal(normalizeUsPhone(input), stored, input);
    assert.equal(formatUsPhone(input), displayed, input);
  }
});

test("rejects short, non-US, letter, and N11 numbers", () => {
  for (const input of ["214555123", "+44 20 7946 0958", "214-555-ABCD", "not-a-phone", "9115551234", "411-555-1234", "214-155-1234"]) {
    assert.equal(normalizeUsPhone(input), "", input);
    assert.equal(formatUsPhone(input), "", input);
  }

  assert.equal(usPhoneErrorMessage, "Please enter a 10-digit US mobile number, like 214-555-1234.");
  assert.equal(displayUsPhone(stored), displayed);
});
