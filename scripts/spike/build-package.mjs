// Rebuilds package.integrity and signs the manifest for a spike package
// (roadmap stage 3.5, leg 1). SPIKE-GRADE: signs with the committed throwaway
// dev key; real packages will be signed by the stage-6 CLI with per-publisher
// keys. Usage: node scripts/spike/build-package.mjs [pkgDir] [keysDir]
import { sign as cryptoSign } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { canonicalize, sha256Hex, listPayloadFiles, readManifest, fingerprintOf, packagePathViolation, devPrivateKey } from './package-ops.mjs';

const pkgDir = path.resolve(process.argv[2] ?? 'spikes/github-stats');
const keysDir = path.resolve(process.argv[3] ?? 'spikes/keys');
const manifestPath = path.join(pkgDir, 'manifest.json');
const manifest = readManifest(pkgDir);

const publicKeyBase64 = fs.readFileSync(path.join(keysDir, 'dev-ed25519-public.b64'), 'utf8').trim();
if (manifest.publisherPublicKey !== publicKeyBase64) {
  throw new Error('manifest.publisherPublicKey does not match the dev key');
}
if (manifest.publisher !== fingerprintOf(publicKeyBase64)) {
  throw new Error('manifest.publisher does not equal sha256(raw public key bytes)');
}

// Hash domain: every payload file; manifest.json participates as its
// canonicalization with signature = null. package.integrity is never
// listed. Files violating the package path grammar abort the build.
const unsigned = { ...manifest, signature: null };
const lines = [];
for (const rel of listPayloadFiles(pkgDir)) {
  const violation = packagePathViolation(rel);
  if (violation) {
    throw new Error(`payload file violates the package path grammar (${violation}): ${rel}`);
  }
  const digest = rel === 'manifest.json'
    ? sha256Hex(Buffer.from(canonicalize(unsigned), 'utf8'))
    : sha256Hex(fs.readFileSync(path.join(pkgDir, rel)));
  lines.push(`${digest}  ${rel}`);
}
const integrityPath = path.join(pkgDir, 'package.integrity');
fs.writeFileSync(integrityPath, lines.join('\n') + '\n', 'utf8');

const contentHash = sha256Hex(fs.readFileSync(integrityPath));
const privateKey = devPrivateKey(keysDir);
// The signature input is the RAW 32-byte digest, never the hex string.
const signature = cryptoSign(null, Buffer.from(contentHash, 'hex'), privateKey);

manifest.signature = {
  contentHash,
  publisherSignature: signature.toString('base64')
};
fs.writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + '\n', 'utf8');
console.log(`built ${path.relative(process.cwd(), pkgDir)}: ${lines.length} integrity lines, contentHash=${contentHash}`);
