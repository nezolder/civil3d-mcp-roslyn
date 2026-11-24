import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";

const root = path.resolve(import.meta.dirname, "..", "..");
const sandbox = fs.readFileSync(path.join(root, "plugin", "Civil3dMcpPlugin", "ScriptSandbox.cs"), "utf8");

function skillFiles(directory) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(directory, entry.name);
    if (entry.isDirectory()) return skillFiles(full);
    return entry.name.endsWith(".skill.md") ? [full] : [];
  });
}

/** Members the plugin refuses in civil3d_query (QueryBlockedMembers). */
function queryBlockedMembers() {
  const block = sandbox.match(/QueryBlockedMembers = new\(\)\s*\{([\s\S]*?)\};/);
  assert.ok(block, "ScriptSandbox.cs declares QueryBlockedMembers");
  return [...block[1].matchAll(/\[\("([\w.]+)", "(\w+)"\)\]/g)].map((m) => ({ type: m[1], member: m[2] }));
}

test("the query-only guard names the section source list", () => {
  const members = queryBlockedMembers();
  assert.ok(
    members.some((m) => m.type === "Autodesk.Civil.DatabaseServices.SampleLineGroup" && m.member === "GetSectionSources")
  );
});

test("no catalog skill or live smoke body uses the short type name Subassembly", () => {
  // A stock subassembly library loaded with a corridor drawing defines a root namespace Subassembly (CS0118).
  const bodies = skillFiles(path.join(root, "skills")).map((file) => [file, fs.readFileSync(file, "utf8").match(/```csharp\r?\n([\s\S]*?)```/)?.[1] ?? ""]);
  const smoke = path.join(root, "scripts", "live-smoke", "csharp");
  for (const name of fs.readdirSync(smoke).filter((n) => n.endsWith(".cs"))) bodies.push([path.join(smoke, name), fs.readFileSync(path.join(smoke, name), "utf8")]);
  for (const [file, code] of bodies) {
    const bare = code.split("\n").filter((line) => !line.trim().startsWith("//") && /(?<![.\w])Subassembly(?![\w.])/.test(line.replace(/"[^"]*"/g, "")));
    assert.deepEqual(bare, [], `${path.relative(root, file)} uses the short name Subassembly`);
  }
});

test("no read-only catalog skill calls a member that civil3d_query refuses", () => {
  const members = queryBlockedMembers();
  for (const file of skillFiles(path.join(root, "skills"))) {
    const text = fs.readFileSync(file, "utf8");
    if (!/^requires_write:\s*false\s*$/m.test(text)) continue;
    const code = text.match(/```csharp\r?\n([\s\S]*?)```/)?.[1] ?? "";
    for (const { member } of members) {
      assert.ok(
        !new RegExp(`\\.${member}\\s*\\(`).test(code),
        `${path.relative(root, file)} runs as a query but calls ${member}`
      );
    }
  }
});
