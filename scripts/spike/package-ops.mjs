// Shared operations for the plugin package spike (roadmap stage 3.5, leg 1).
// SPIKE-GRADE tooling: Node-only, zero dependencies, exercise the schema
// v0.2 hash/signature chain end to end. The stage-6 CLI validator will be
// a .NET AOT exe with a full JCS implementation; anything here must be
// treated as scaffolding, not the reference implementation.
import { createHash, createPublicKey } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

// DER SPKI header for a raw Ed25519 public key (12 bytes + 32 raw).
const ED25519_SPKI_PREFIX = Buffer.from([
  0x30, 0x2a, 0x30, 0x05, 0x06, 0x03, 0x2b, 0x65, 0x70, 0x03, 0x21, 0x00
]);

export function ed25519PublicKey(publicKeyBase64) {
  const raw = Buffer.from(publicKeyBase64, 'base64');
  if (raw.length !== 32) {
    throw new Error(`Ed25519 public key must be 32 raw bytes, got ${raw.length}`);
  }
  return createPublicKey({
    key: Buffer.concat([ED25519_SPKI_PREFIX, raw]),
    format: 'der',
    type: 'spki'
  });
}

export function sha256Hex(buf) {
  return createHash('sha256').update(buf).digest('hex');
}

// JCS-subset canonicalization (RFC 8785 restricted to manifest shapes):
// key sorting by UTF-16 code units (JS default sort), no whitespace, ES
// number formatting. Fractional numbers are rejected loudly instead of
// being silently misformatted - spike manifests use integers only.
export function canonicalize(value) {
  if (value === null || typeof value === 'boolean' || typeof value === 'string') {
    return JSON.stringify(value);
  }
  if (typeof value === 'number') {
    if (!Number.isInteger(value)) {
      throw new Error('spike JCS subset: fractional numbers are not supported');
    }
    return JSON.stringify(value);
  }
  if (Array.isArray(value)) {
    return '[' + value.map(canonicalize).join(',') + ']';
  }
  const keys = Object.keys(value).sort();
  return '{' + keys.map(k => JSON.stringify(k) + ':' + canonicalize(value[k])).join(',') + '}';
}

// Enumerates package payload files (forward-slash relative paths), excluding
// package.integrity itself. Ordinal-sorted by path.
export function listPayloadFiles(pkgDir) {
  const files = [];
  const walk = dir => {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        walk(full);
      } else {
        files.push(path.relative(pkgDir, full).split(path.sep).join('/'));
      }
    }
  };
  walk(pkgDir);
  return files.filter(f => f !== 'package.integrity').sort();
}

export function readManifest(pkgDir) {
  return JSON.parse(fs.readFileSync(path.join(pkgDir, 'manifest.json'), 'utf8'));
}

export function fingerprintOf(publicKeyBase64) {
  const raw = Buffer.from(publicKeyBase64, 'base64');
  if (raw.length !== 32) {
    throw new Error(`Ed25519 public key must be 32 raw bytes, got ${raw.length}`);
  }
  return sha256Hex(raw);
}