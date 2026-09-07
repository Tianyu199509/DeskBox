// Validates a spike package against schema v0.2 semantics and verifies the
// full integrity/signature chain (plugin-schema-v0-notes.md walkthrough,
// steps 1-5). SPIKE-GRADE structural validation hand-rolls the pinned v0.2
// rules; the stage-6 CLI will do full JSON Schema validation.
// Usage: node scripts/spike/validate-package.mjs [pkgDir]
// Exit 0 + "VERIFIED" on success; exit 1 with a failure list otherwise.
import { verify as cryptoVerify } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { canonicalize, sha256Hex, listPayloadFiles, readManifest, fingerprintOf, ed25519PublicKey } from './package-ops.mjs';

const pkgDir = path.resolve(process.argv[2] ?? 'spikes/github-stats');
const failures = [];
const fail = msg => failures.push(msg);

// ---------- structural validation (schema v0.2 pinned rules) ----------
const manifest = readManifest(pkgDir);
const rootRequired = ['schemaVersion', 'id', 'version', 'publisher', 'publisherPublicKey', 'runtime', 'hostApi', 'contributions'];
for (const key of rootRequired) {
  if (!(key in manifest)) fail(`root: missing required '${key}'`);
}
const rootClosed = new Set([...rootRequired, 'permissions', 'data', 'signature', 'fallback']);
for (const key of Object.keys(manifest)) {
  if (!rootClosed.has(key)) fail(`root: unknown property '${key}'`);
}
if (manifest.schemaVersion !== 0) fail('schemaVersion must be 0');
if (!/^[a-z0-9][a-z0-9-]*(\.[a-z0-9][a-z0-9-]*)+$/.test(manifest.id ?? '')) fail('id pattern');
if (!/^\d+\.\d+\.\d+$/.test(manifest.version ?? '')) fail('version pattern');
if (!['none', 'wasm', 'process'].includes(manifest.runtime)) fail('runtime enum');
if (manifest.publisher === undefined || manifest.publisher === '') fail('publisher missing');
if (typeof manifest.publisherPublicKey !== 'string' || manifest.publisherPublicKey === '') fail('publisherPublicKey missing');

const hostApi = manifest.hostApi ?? {};
if (typeof hostApi.min !== 'string' || typeof hostApi.max !== 'string') fail('hostApi.min/max required');
if (Object.keys(hostApi).some(k => !['min', 'max'].includes(k))) fail('hostApi closed');

const contributions = manifest.contributions;
if (!Array.isArray(contributions) || contributions.length < 1) fail('contributions: minItems 1');
const widgetClosed = ['type', 'id', 'displayName', 'template', 'payload', 'defaultSize', 'activationEvents'];
const templates = ['metric', 'list', 'status', 'gallery', 'action-list', 'simple-form'];
(contributions ?? []).forEach((c, i) => {
  const where = `contributions[${i}]`;
  for (const key of ['type', 'id', 'displayName', 'template']) {
    if (!(key in c)) fail(`${where}: missing required '${key}'`);
  }
  for (const key of Object.keys(c)) {
    if (!widgetClosed.includes(key)) fail(`${where}: unknown property '${key}'`);
  }
  if (c.type !== undefined && c.type !== 'widget') fail(`${where}: unknown contribution type`);
  if (c.id !== undefined && !/^[a-z0-9][a-z0-9-]*$/.test(c.id)) fail(`${where}: id pattern`);
  if (c.displayName !== undefined && c.displayName === '') fail(`${where}: displayName minLength 1`);
  if (c.template !== undefined && !templates.includes(c.template)) fail(`${where}: template enum`);
  if (c.payload !== undefined) {
    if (!(typeof c.payload.version === 'number' && Number.isInteger(c.payload.version) && c.payload.version >= 1)) {
      fail(`${where}.payload.version >= 1 required`);
    }
  }
});
const ids = (contributions ?? []).map(c => c.id);
if (new Set(ids).size !== ids.length) fail('contribution ids must be unique within the package');

(manifest.permissions ?? []).forEach((p, i) => {
  if (!/^[a-z0-9-]+(\.[a-z0-9-]+)+$/.test(p.id ?? '')) fail(`permissions[${i}]: id pattern`);
  for (const key of Object.keys(p)) {
    if (!['id', 'required', 'scope'].includes(key)) fail(`permissions[${i}]: unknown property '${key}'`);
  }
});

const signature = manifest.signature;
if (signature !== null && signature !== undefined) {
  for (const key of ['contentHash', 'publisherSignature']) {
    if (typeof signature[key] !== 'string' || signature[key] === '') {
      fail(`signature: '${key}' required when the block is present`);
    }
  }
  for (const key of Object.keys(signature)) {
    if (!['contentHash', 'publisherSignature'].includes(key)) fail(`signature: unknown property '${key}'`);
  }
}

// ---------- verification chain (notes walkthrough 1-5) ----------
const integrityPath = path.join(pkgDir, 'package.integrity');
const integrityText = fs.existsSync(integrityPath)
  ? fs.readFileSync(integrityPath, 'utf8')
  : null;
const listed = new Map();
if (integrityText !== null) {
  for (const line of integrityText.split('\n')) {
    if (line === '') continue;
    const m = line.match(/^([0-9a-f]{64})  (.+)$/);
    if (!m) { fail(`package.integrity: malformed line '${line.slice(0, 40)}'`); continue; }
    listed.set(m[2], m[1]);
  }
} else {
  fail('package.integrity missing');
}

// Step 1: the manifest's canonical form (signature = null) must match its
// integrity line - catches a manifest edited after the integrity was built.
const unsigned = { ...manifest, signature: null };
const canonicalManifestHash = sha256Hex(Buffer.from(canonicalize(unsigned), 'utf8'));
if (listed.get('manifest.json') !== canonicalManifestHash) {
  fail('manifest.json integrity line does not match its canonicalization (signature=null)');
}

// Step 2: every listed payload file must hash to its line, and every payload
// file on disk must be listed (tamper detection + completeness).
for (const rel of listPayloadFiles(pkgDir)) {
  const digest = rel === 'manifest.json'
    ? canonicalManifestHash
    : sha256Hex(fs.readFileSync(path.join(pkgDir, rel)));
  if (!listed.has(rel)) {
    fail(`payload file not listed in package.integrity: ${rel}`);
  } else if (listed.get(rel) !== digest) {
    fail(`integrity mismatch for ${rel}`);
  }
}
for (const rel of listed.keys()) {
  if (rel !== 'manifest.json' && !fs.existsSync(path.join(pkgDir, rel))) {
    fail(`package.integrity lists a missing file: ${rel}`);
  }
}

if (signature && integrityText !== null) {
  // Step 3: contentHash over the package.integrity bytes.
  const contentHash = sha256Hex(fs.readFileSync(integrityPath));
  if (signature.contentHash !== contentHash) {
    fail(`signature.contentHash mismatch (expected ${contentHash})`);
  }

  // Step 4: Ed25519 over the RAW 32-byte digest with publisherPublicKey.
  let verified = false;
  try {
    verified = cryptoVerify(
      null,
      Buffer.from(signature.contentHash, 'hex'),
      ed25519PublicKey(manifest.publisherPublicKey),
      Buffer.from(signature.publisherSignature, 'base64'));
  } catch {
    verified = false;
  }
  if (!verified) fail('publisherSignature verification failed');

  // Step 5: publisher == sha256(raw public key bytes), lowercase hex.
  if (manifest.publisher !== fingerprintOf(manifest.publisherPublicKey)) {
    fail('publisher does not equal sha256(raw publisherPublicKey bytes)');
  }
} else if (!signature) {
  console.log('note: unsigned dev package - steps 3-5 skipped, steps 1-2 still enforced');
}

if (failures.length > 0) {
  console.error(`INVALID (${failures.length} failures):`);
  for (const f of failures) console.error(`  - ${f}`);
  process.exit(1);
}
console.log('VERIFIED: structural + integrity + signature chain OK');
