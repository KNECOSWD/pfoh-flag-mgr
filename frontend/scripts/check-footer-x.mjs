import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const app = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
const css = readFileSync(new URL("../src/style.css", import.meta.url), "utf8");

test("the shared footer links to Plano Flags of Honor on X", () => {
  assert.equal(app.split('className="appFooter footerNavy"').length - 1, 1, "one footer for every signed-in state");

  const socialsAt = app.indexOf('className="footerSocials"');
  assert.ok(socialsAt >= 0, "social links live in the footer");
  const socials = app.slice(socialsAt, app.indexOf("</div>", socialsAt));

  assert.match(socials, /href="https:\/\/x\.com\/FlagsHonor"/);
  assert.match(socials, /target="_blank"/);
  assert.match(socials, /rel="noopener noreferrer"/);
  assert.match(socials, /aria-label="Plano Flags of Honor on X"/);
  assert.match(socials, /aria-hidden="true"/);
  assert.equal(app.split('href="https://x.com/FlagsHonor"').length - 1, 1);
  assert.equal(app.split('aria-label="Plano Flags of Honor on X"').length - 1, 1);

  assert.match(css, /--footer-social-size:\s*2\.75rem/);
  assert.match(css, /--footer-social-icon-size:\s*1\.05rem/);
  const widths = [...css.matchAll(/\.footerSocials a \{[^}]*?(?<![\w-])width:\s*([^;]+);/gs)];
  assert.equal(widths.length, 1, "the social size is set once");
  assert.equal(widths[0][1].trim(), "var(--footer-social-size)");
  assert.doesNotMatch(widths[0][0], /!important/);
  const icon = css.match(/\.footerSocials a svg \{[^}]*\}/s);
  assert.ok(icon, "the X mark is sized with the icon token");
  assert.match(icon[0], /var\(--footer-social-icon-size\)/);
  assert.doesNotMatch(icon[0], /!important/);
});
