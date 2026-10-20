#!/usr/bin/env node
// Contract-vector gate, run in every SDK's CI (Q19). Canonical copy: shared-spec/scripts/spec-vectors.mjs;
// every SDK carries a byte-identical copy (scripts/check-parity.sh checks it), like brand.mjs.
//
// Reads spec-pin.json at the repo root:
//   specCommit      the shared-spec commit the vendored vectors were copied from
//   files           { signature|conformance: { path, sha256 } }: each vendored copy and its pinned hash
//   conformanceTest the test file that must exercise every conformance group
//   exempt          { group: "contract reason" }: groups this SDK does not implement, by contract
//   note            what to print when no vectors apply (the Node server SDK)
// Fails when a vendored file differs from its pin, or when a conformance group is neither named in the
// test nor exempt (so a new vector group can't be silently skipped). Prints the counts for the CI log.
//   node scripts/spec-vectors.mjs
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';

const pin = JSON.parse(readFileSync('spec-pin.json', 'utf8'));
const read = (p) => readFileSync(p);
const sha256 = (p) => createHash('sha256').update(read(p)).digest('hex');
const problems = [];
const parts = [];

for (const f of Object.values(pin.files ?? {})) {
  const got = sha256(f.path);
  if (got !== f.sha256) {
    problems.push(`${f.path} differs from shared-spec ${pin.specCommit} (sha256 ${got.slice(0, 12)}, pinned ${f.sha256.slice(0, 12)}): copy it from shared-spec and update spec-pin.json`);
  }
}

if (pin.files?.signature) {
  const v = JSON.parse(read(pin.files.signature.path));
  parts.push(`signatures ${v.signatures.length} · h32 ${v.h32.length}`);
}

if (pin.files?.conformance) {
  const v = JSON.parse(read(pin.files.conformance.path));
  const groups = Object.keys(v).filter((k) => Array.isArray(v[k]));
  const exempt = pin.exempt ?? {};
  const tested = groups.filter((g) => !exempt[g]);
  const cases = (gs) => gs.reduce((n, g) => n + v[g].length, 0);
  if (pin.conformanceTest) {
    const src = read(pin.conformanceTest).toString('utf8');
    for (const g of tested) {
      if (!new RegExp(`["'.\\[]${g}\\b`).test(src)) problems.push(`${pin.conformanceTest} never names conformance group "${g}": test it, or list it under "exempt" in spec-pin.json with the contract reason`);
    }
    parts.push(`conformance v${v.version}: ${cases(tested)} cases in ${tested.length} of ${groups.length} groups`);
    const ex = groups.filter((g) => exempt[g]);
    if (ex.length) parts.push(`exempt ${ex.map((g) => `${g} (${exempt[g]})`).join(', ')}`);
  } else {
    parts.push(`conformance v${v.version} carried (${cases(groups)} cases), not run here: ${pin.note ?? 'no conformance test'}`);
  }
}

if (!parts.length) parts.push(pin.note ?? 'no vectors apply');
for (const p of problems) console.error(`✗ ${p}`);
console.log(`contract vectors @ shared-spec ${pin.specCommit}: ${parts.join(' · ')} — ${problems.length ? `${problems.length} problem(s)` : 'pin OK'}`);
process.exit(problems.length ? 1 : 0);
