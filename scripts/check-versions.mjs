// Fails when the app version differs between Directory.Build.props, the installer default and the winget manifests.
// winget files may use the {{VERSION}} placeholder (filled by scripts/winget.ps1) or a literal that must match.
//   node scripts/check-versions.mjs
import { existsSync, readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";

const root = join(import.meta.dirname, "..");
const read = (p) => readFileSync(join(root, p), "utf8");
const props = /<Version>([^<]+)<\/Version>/.exec(read("Directory.Build.props"))?.[1]?.trim();
if (!props) { console.error("Directory.Build.props: no <Version> found"); process.exit(1); }

const errors = [];
const iss = /#define\s+AppVersion\s+"([^"]+)"/.exec(read("installer/Umbra.iss"))?.[1];
if (iss !== props) errors.push(`installer/Umbra.iss default AppVersion is ${iss ?? "missing"}, Directory.Build.props is ${props}`);

for (const f of readdirSync(join(root, "winget")).filter((n) => n.endsWith(".yaml"))) {
  const text = read(`winget/${f}`);
  for (const m of text.matchAll(/^PackageVersion:\s*"?([^"\s]+)"?/gm)) {
    if (m[1] !== "{{VERSION}}" && m[1] !== props) errors.push(`winget/${f} PackageVersion is ${m[1]}, expected ${props} or {{VERSION}}`);
  }
  for (const m of text.matchAll(/(?:download\/v|UmbraSetup-|tag\/v)(\d[\w.]*)/g)) {
    if (m[1] !== props) errors.push(`winget/${f} hardcodes version ${m[1]}, expected ${props} or {{VERSION}}`);
  }
}

// The Linux AppStream metainfo (#399, shipped in the tarball and used by Flatpak/Flathub, #196): its newest <release> must
// be this version. release.mjs adds that entry with every version bump.
const metainfo = "linux/io.github.aminechr54.Umbra.metainfo.xml";
if (existsSync(join(root, metainfo))) {
  const rel = /<release\b[^>]*\bversion="([^"]+)"/.exec(read(metainfo))?.[1];
  if (rel !== props) errors.push(`${metainfo} newest <release> is ${rel ?? "missing"}, Directory.Build.props is ${props}`);
}

if (errors.length) { console.error("Version mismatch:\n  " + errors.join("\n  ")); process.exit(1); }
console.log(`Versions consistent: ${props}`);
