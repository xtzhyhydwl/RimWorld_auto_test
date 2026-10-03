import fs from 'node:fs';
const t = JSON.parse(fs.readFileSync(new URL('../runs/debug-tree-latest.json', import.meta.url), 'utf8'));
const all = Object.values(t.nodes);
const parent = 'Actions\\Spawn thing...';
const kids = all.filter((n) => n.parentPath === parent);
const re = new RegExp(process.argv[2] ?? '.', 'i');
const hits = kids.filter((n) => re.test(n.label || ''));
console.log(`${hits.length} / ${kids.length} match ${re}`);
for (const n of hits) console.log(`  ${n.label}`);
