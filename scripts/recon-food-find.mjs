import { connectGabp } from "../src/gabp.js";
import { readBridgeInfo, logPathFor } from "../src/launcher.js";
const info = readBridgeInfo(logPathFor("vanilla"));
const { client } = await connectGabp(info, { requestTimeoutMs: 200000 });
const call = (n,a,o)=>client.callTool(n,a,o);
let found=null;
for (let z=0; z<250 && !found; z+=32) for (let x=0; x<250 && !found; x+=32) {
  const r = await call("rimworld/get_cells_info",{x,z,width:32,height:32});
  for (const c of r.cells||[]) for (const t of c.things||[]) if (/^Meal/.test(t.defName||"")) { found={x:c.x,z:c.z,def:t.defName}; break; }
}
console.log("food at", JSON.stringify(found));
if (found) {
  const ci = await call("rimworld/get_cell_info",{x:found.x,z:found.z});
  console.log(JSON.stringify(ci.cell));
}
client.close();
