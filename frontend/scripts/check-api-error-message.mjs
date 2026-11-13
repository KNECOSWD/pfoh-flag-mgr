import assert from "node:assert/strict";
import test from "node:test";
import { chooseServerMessage } from "../src/apiErrorMessage.js";

test("a bio length error is the message even when the server also sent a title", () => {
  const message = chooseServerMessage({
    title: "One or more validation errors occurred.",
    message: "The submission was rejected.",
    errors: { Description: ["Bio must be 600 characters or fewer."] }
  }, "raw");
  assert.equal(message, "Bio must be 600 characters or fewer.");
});

test("another field error does not outrank the server message", () => {
  const message = chooseServerMessage({
    title: "One or more validation errors occurred.",
    message: "The phone number is not valid.",
    errors: { FirstName: ["First name is required."] }
  }, "raw");
  assert.equal(message, "The phone number is not valid.");
});

test("another field error does not outrank the problem title", () => {
  const message = chooseServerMessage({
    title: "One or more validation errors occurred.",
    errors: { SubmitterPhoneNumber: ["Bad phone."] }
  }, "raw");
  assert.equal(message, "One or more validation errors occurred.");
});

test("a response with no field errors keeps the server message", () => {
  assert.equal(chooseServerMessage({ message: "Honoree was not found." }, "raw"), "Honoree was not found.");
});
