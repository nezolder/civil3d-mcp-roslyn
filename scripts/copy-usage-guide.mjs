// Copies the usage guide next to the compiled server. An installed build has
// only build/, so the guide the server hands to the assistant travels with it.
import { copyFileSync, mkdirSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const source = path.join(repoRoot, "docs", "USAGE.md");
const target = path.join(repoRoot, "build", "USAGE.md");

mkdirSync(path.dirname(target), { recursive: true });
copyFileSync(source, target);
console.log(`Usage guide copied to ${path.relative(repoRoot, target)}`);
