import fs from "node:fs";
import path from "node:path";
const dir = "runs/food-recon";
const out = {};
for (const f of fs.readdirSync(dir)) {
  if (!f.endsWith(".json") || f === "summary.json" || f === "all-evidence.json") continue;
  try { out[f.replace(/\.json$/, "")] = JSON.parse(fs.readFileSync(path.join(dir, f), "utf8")); }
  catch (e) { out[f] = { parseError: e.message }; }
}
fs.writeFileSync(path.join(dir, "all-evidence.json"), JSON.stringify(out, null, 2), "utf8");
console.log("merged", Object.keys(out).join(", "));
console.log("bytes", fs.statSync(path.join(dir, "all-evidence.json")).size);
