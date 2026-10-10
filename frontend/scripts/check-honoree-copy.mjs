import { readFileSync, readdirSync, statSync } from "node:fs";
import { dirname, join, relative } from "node:path";
import { fileURLToPath } from "node:url";

const frontendRoot = join(dirname(fileURLToPath(import.meta.url)), "..");
const repoRoot = join(frontendRoot, "..");
const files = [join(frontendRoot, "index.html")];

function addMatching(directory, extensions) {
  for (const entry of readdirSync(directory)) {
    const path = join(directory, entry);
    if (statSync(path).isDirectory()) {
      addMatching(path, extensions);
      continue;
    }

    if (extensions.some((extension) => path.endsWith(extension))) {
      files.push(path);
    }
  }
}

addMatching(join(frontendRoot, "src"), [".tsx", ".ts", ".css", ".html"]);
addMatching(join(frontendRoot, "public"), [".html", ".webmanifest", ".json"]);

const failures = [];
let aliasCount = 0;

for (const file of files) {
  const repoPath = relative(repoRoot, file).split("\\").join("/");
  const lines = readFileSync(file, "utf8").split(/\r?\n/);
  lines.forEach((original, index) => {
    const hits = original.match(/submit-veteran/g)?.length ?? 0;
    let line = original;
    if (hits > 0) {
      if (repoPath !== "frontend/src/App.tsx") {
        failures.push(`${repoPath}:${index + 1}: the legacy alias is only allowed in App.tsx`);
      }

      aliasCount += hits;
      line = line.replaceAll("submit-veteran", "");
    }

    if (/veteran/i.test(line)) {
      failures.push(`${repoPath}:${index + 1}: ${original.trim()}`);
    }
  });
}

if (aliasCount !== 1) {
  failures.push(`Expected the legacy alias submit-veteran once in frontend/src/App.tsx, found ${aliasCount}.`);
}

if (failures.length > 0) {
  console.error(failures.join("\n"));
  process.exit(1);
}

console.log("User-facing frontend copy has no veteran wording. The legacy submit-veteran alias is present once.");
