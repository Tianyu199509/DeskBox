// Shared operations for the plugin package spike (roadmap stage 3.5, leg 1).
// SPIKE-GRADE tooling: Node-only, zero dependencies, exercise the schema
// v0.2 hash/signature chain end to end. The stage-6 CLI validator will be
// a .NET AOT exe with a full JCS implementation; anything here must be
// treated as scaffolding, not the reference implementation.
import { createHash, createPrivateKey, createPublicKey } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

// Package path grammar (platform protocol, pinned early against zip-slip):
// relative, forward slashes only, no . / .. segments, no drive prefix, no
// colon (NTFS ADS), no empty segments.
// Windows-safe path grammar (mirrors the C# verifier; round 10): relative
// forward-slash paths only, no .. / . / empty / backslash / colon / control
// chars, no trailing dot/space, no reserved DOS device names (incl. with
// extensions) - the filesystem and the integrity strings must denote the
// same object.
const RESERVED_DEVICE_NAMES = new Set([
  'CON', 'PRN', 'AUX', 'NUL',
  'COM1', 'COM2', 'COM3', 'COM4', 'COM5', 'COM6', 'COM7', 'COM8', 'COM9',
  'LPT1', 'LPT2', 'LPT3', 'LPT4', 'LPT5', 'LPT6', 'LPT7', 'LPT8', 'LPT9'
]);

export function packagePathViolation(rel) {
  if (rel.startsWith('/') || rel.includes('\\') || rel.includes(':')) {
    return 'rooted path, backslash, or colon';
  }
  const segments = rel.split('/');
  for (const seg of segments) {
    if (seg === '' || seg === '.' || seg === '..') {
      return "empty, '.', or '..' segment";
    }
    if (/[ .]$/.test(seg)) {
      return 'segment ending in space or dot';
    }
    if (/[\x00-\x1f]/.test(seg)) {
      return 'control character in segment';
    }
    const base = seg.split('.')[0].toUpperCase();
    if (RESERVED_DEVICE_NAMES.has(base)) {
      return `reserved Windows device name '${base}'`;
    }
  }
  return null;
}

// Builds the spike dev private key from the committed 32-byte seed hex.
// A seed text file (clearly a test vector) is preferred over a PEM-shaped
// private key blob, which secret scanners reliably flag as a finding.
export function devPrivateKey(keysDir) {
  const seedHex = fs.readFileSync(path.join(keysDir, 'dev-ed25519-seed.txt'), 'utf8').trim();
  const seed = Buffer.from(seedHex, 'hex');
  if (seed.length !== 32) {
    throw new Error(`dev seed must be 32 bytes, got ${seed.length}`);
  }
  const pkcs8 = Buffer.concat([
    Buffer.from('302e020100300506032b657004220420', 'hex'),
    seed
  ]);
  return createPrivateKey({ key: pkcs8, format: 'der', type: 'pkcs8' });
}

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