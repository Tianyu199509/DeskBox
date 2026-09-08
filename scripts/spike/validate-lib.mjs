// Structural validation + verification chain for spike packages, shared by
// the validate-package CLI and the declarative run harness (roadmap stage
// 3.5). SPIKE-GRADE: hand-rolled v0.2/v0.3 rules; the stage-6 CLI will do
// full JSON Schema validation with a complete JCS implementation.
import fs from 'node:fs';
import path from 'node:path';
import { canonicalize, sha256Hex, listPayloadFiles, readManifest, fingerprintOf, ed25519PublicKey, packagePathViolation } from './package-ops.mjs';
import { verify as cryptoVerify } from 'node:crypto';

export function isPackageVersion(value) {
  return typeof value === 'string' && value.length <= 32 && /^[0-9]+\.[0-9]+\.[0-9]+$/.test(value) &&
    value.split('.').every(part => Number(part) <= 2147483647);
}

export function isMinimalJsonPath(value) {
  if (typeof value !== 'string' || value.length > 512 ||
      !/^\$\.[A-Za-z0-9_]+(?:\[[0-9]+\])*(?:\.[A-Za-z0-9_]+(?:\[[0-9]+\])*)*$/.test(value)) return false;
  const tokens = [...value.slice(2).matchAll(/([A-Za-z0-9_]+)|\[([0-9]+)\]/g)];
  return tokens.length <= 64 && tokens.every(token => token[2] === undefined || Number(token[2]) <= 2147483647);
}

export function validatePackage(pkgDir) {
  const failures = [];
  const fail = msg => failures.push(msg);
  const manifest = readManifest(pkgDir);

  // ---------- structural validation (schema v0.2 + v0.3 pinned rules) ----------
  const rootRequired = ['schemaVersion', 'id', 'version', 'publisher', 'publisherPublicKey', 'runtime', 'hostApi', 'contributions'];
  for (const key of rootRequired) {
    if (!(key in manifest)) fail(`root: missing required '${key}'`);
  }
  const rootClosed = new Set([...rootRequired, 'permissions', 'data', 'signature', 'fallback', 'dataSources', 'actions', 'entry']);
  for (const key of Object.keys(manifest)) {
    if (!rootClosed.has(key)) fail(`root: unknown property '${key}'`);
  }
  if (manifest.schemaVersion !== 0) fail('schemaVersion must be 0');
  if (!/^[a-z0-9][a-z0-9-]*(\.[a-z0-9][a-z0-9-]*)+$/.test(manifest.id ?? '')) fail('id pattern');
  if (!isPackageVersion(manifest.version)) fail('version pattern or bounded version range');
  if (!['none', 'wasm', 'process'].includes(manifest.runtime)) fail('runtime enum');
  if (manifest.runtime !== 'none' && manifest.entry === undefined) {
    fail(`runtime '${manifest.runtime}' requires an entry point`);
  }
  if (manifest.entry !== undefined) {
    if (manifest.runtime === 'none') fail('runtime:none packages must not declare an entry point');
    if (typeof manifest.entry?.main !== 'string' || manifest.entry.main === '') {
      fail('entry.main must be a non-empty string');
    }
    for (const key of Object.keys(manifest.entry ?? {})) {
      if (key !== 'main') fail(`entry: unknown property '${key}'`);
    }
    if (typeof manifest.entry?.main === 'string') {
      const violation = packagePathViolation(manifest.entry.main);
      if (violation) fail(`entry.main violates the package path grammar (${violation})`);
      else if (!fs.existsSync(path.join(pkgDir, manifest.entry.main))) {
        fail(`entry.main file not found in package: ${manifest.entry.main}`);
      }
    }
  }
  if (manifest.publisher === undefined || manifest.publisher === '') fail('publisher missing');
  if (typeof manifest.publisherPublicKey !== 'string' || manifest.publisherPublicKey === '') fail('publisherPublicKey missing');

  const hostApi = manifest.hostApi ?? {};
  if (!isPackageVersion(hostApi.min) || !isPackageVersion(hostApi.max)) fail('hostApi.min/max bounded versions required');
  else {
    const minimum = hostApi.min.split('.').map(Number), maximum = hostApi.max.split('.').map(Number);
    const firstDifference = minimum.findIndex((part, i) => part !== maximum[i]);
    if (firstDifference >= 0 && minimum[firstDifference] > maximum[firstDifference]) fail('hostApi.min must not exceed max');
  }
  if (Object.keys(hostApi).some(k => !['min', 'max'].includes(k))) fail('hostApi closed');

  const contributions = manifest.contributions;
  if (!Array.isArray(contributions) || contributions.length < 1) fail('contributions: minItems 1');
  const widgetClosed = ['type', 'id', 'displayName', 'template', 'payload', 'bindings', 'defaultSize', 'activationEvents'];
  const templates = ['metric', 'list', 'status', 'gallery', 'action-list', 'simple-form'];
  const dataSources = manifest.dataSources ?? {};
  const actions = manifest.actions ?? {};
  const localIdPattern = /^[a-z0-9][a-z0-9-]*$/;
  for (const key of Object.keys(dataSources)) {
    if (!localIdPattern.test(key)) fail(`dataSources: key '${key}' must use the local id pattern (^[a-z0-9][a-z0-9-]*$)`);
  }
  for (const key of Object.keys(actions)) {
    if (!localIdPattern.test(key)) fail(`actions: key '${key}' must use the local id pattern (^[a-z0-9][a-z0-9-]*$)`);
  }
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
    if (c.defaultSize !== undefined) {
      const size = c.defaultSize;
      if (!size || typeof size !== 'object' || Array.isArray(size) ||
          !['width', 'height'].every(k => Number.isInteger(size[k]) && size[k] > 0 && size[k] <= 2147483647) ||
          Object.keys(size).some(k => !['width', 'height'].includes(k))) fail(`${where}: invalid defaultSize`);
    }
    if (c.activationEvents !== undefined && (!Array.isArray(c.activationEvents) ||
        c.activationEvents.some(e => !['onStartupFinished', 'onWidgetOpen', 'onCommand', 'onSchedule', 'onFileAssociation', 'onEvent'].includes(e)))) {
      fail(`${where}: invalid activationEvents`);
    }
    if (c.payload !== undefined) {
      if (!(typeof c.payload.version === 'number' && Number.isInteger(c.payload.version) && c.payload.version >= 1)) {
        fail(`${where}.payload.version >= 1 required`);
      }
    }
    if (c.bindings !== undefined) {
      for (const [field, binding] of Object.entries(c.bindings)) {
        const bWhere = `${where}.bindings['${field}']`;
        for (const key of ['source', 'path']) {
          if (typeof binding?.[key] !== 'string' || binding[key] === '') fail(`${bWhere}: '${key}' required`);
        }
        for (const key of Object.keys(binding ?? {})) {
          if (!['source', 'path'].includes(key)) fail(`${bWhere}: unknown property '${key}'`);
        }
        if (binding?.source !== undefined && !(binding.source in dataSources)) {
          fail(`${bWhere}: unknown data source '${binding.source}'`);
        }
        if (binding?.path !== undefined && !isMinimalJsonPath(binding.path)) {
          fail(`${bWhere}: path must be a minimal JSON path like $.a.b[0].c`);
        }
        if (c.payload !== undefined && !(field in c.payload)) {
          fail(`${bWhere}: binds a field the payload does not have`);
        }
      }
    }
  });
  const ids = (contributions ?? []).map(c => c.id);
  if (new Set(ids).size !== ids.length) fail('contribution ids must be unique within the package');

  // v0.3 data sources: http-json only, HTTPS only, sane refresh interval.
  for (const [id, source] of Object.entries(dataSources)) {
    const where = `dataSources['${id}']`;
    for (const key of ['type', 'url', 'refreshSeconds']) {
      if (!(key in (source ?? {}))) fail(`${where}: missing required '${key}'`);
    }
    for (const key of Object.keys(source ?? {})) {
      if (!['type', 'url', 'refreshSeconds'].includes(key)) fail(`${where}: unknown property '${key}'`);
    }
    if (source?.type !== undefined && source.type !== 'http-json') fail(`${where}: v0.3 supports type http-json only`);
    if (typeof source?.url === 'string' && !source.url.startsWith('https://')) fail(`${where}: url must be HTTPS`);
    if (source?.refreshSeconds !== undefined &&
        !(Number.isInteger(source.refreshSeconds) && source.refreshSeconds >= 10)) {
      fail(`${where}: refreshSeconds must be an integer >= 10`);
    }
  }

  // v0.3 actions: open-url only, HTTPS only; payload actionIds must resolve
  // when the actions map is present (v0.2 packages without a map are exempt).
  for (const [id, action] of Object.entries(actions)) {
    const where = `actions['${id}']`;
    for (const key of ['type', 'url']) {
      if (!(key in (action ?? {}))) fail(`${where}: missing required '${key}'`);
    }
    for (const key of Object.keys(action ?? {})) {
      if (!['type', 'url'].includes(key)) fail(`${where}: unknown property '${key}'`);
    }
    if (action?.type !== undefined && action.type !== 'open-url') fail(`${where}: v0.3 supports type open-url only`);
    if (typeof action?.url === 'string' && !action.url.startsWith('https://')) fail(`${where}: url must be HTTPS`);
  }
  if (Object.keys(actions).length > 0) {
    const referenced = new Set();
    for (const c of contributions ?? []) {
      if (typeof c.payload?.primaryActionId === 'string') referenced.add(c.payload.primaryActionId);
      for (const value of Object.values(c.payload ?? {})) {
        if (Array.isArray(value)) {
          for (const entry of value) {
            if (typeof entry?.actionId === 'string') referenced.add(entry.actionId);
          }
        } else if (typeof value?.actionId === 'string') {
          referenced.add(value.actionId);
        }
      }
    }
    for (const actionId of referenced) {
      if (!(actionId in actions)) fail(`payload actionId '${actionId}' does not resolve to an actions entry`);
    }
  }

  const permissions = manifest.permissions ?? [];
  const seenPermissionIds = new Set();
  permissions.forEach((p, i) => {
    if (!/^[a-z0-9-]+(\.[a-z0-9-]+)+$/.test(p.id ?? '')) fail(`permissions[${i}]: id pattern`);
    if (seenPermissionIds.has(p.id)) {
      // Duplicate ids with different scopes would make grant/scope lookup
      // implementation-defined (first match? merge?) across the three
      // future runtimes - one entry per id, multiple hosts inside it.
      fail(`permissions: duplicate id '${p.id}' (use one entry with multiple scope.allow hosts)`);
    }
    seenPermissionIds.add(p.id);
    for (const key of Object.keys(p)) {
      if (!['id', 'required', 'scope'].includes(key)) fail(`permissions[${i}]: unknown property '${key}'`);
    }
  });
  const permissionIds = seenPermissionIds;
  const scopeAllows = id => {
    const found = permissions.find(p => p.id === id);
    return found?.scope?.allow ?? [];
  };

  // v0.3 permission consumption: the capabilities a package asks the host to
  // execute must be declared, and the concrete URLs must fall inside scope.
  const hostInScope = (url, id) => {
    try {
      const host = new URL(url).hostname.toLowerCase();
      return scopeAllows(id).some(entry => entry.toLowerCase() === host);
    } catch {
      return false;
    }
  };
  for (const [id, source] of Object.entries(dataSources)) {
    if (source?.type === 'http-json') {
      if (!permissionIds.has('network.fetch')) {
        fail(`dataSources['${id}']: http-json requires the network.fetch permission`);
      } else if (typeof source.url === 'string' && !hostInScope(source.url, 'network.fetch')) {
        fail(`dataSources['${id}']: url host is outside the declared network.fetch scope`);
      }
    }
  }
  for (const [id, action] of Object.entries(actions)) {
    if (action?.type === 'open-url') {
      if (!permissionIds.has('shell.open')) {
        fail(`actions['${id}']: open-url requires the shell.open permission`);
      } else if (typeof action.url === 'string' && !hostInScope(action.url, 'shell.open')) {
        fail(`actions['${id}']: url host is outside the declared shell.open scope`);
      }
    }
  }

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
    const seenNormalized = new Map();
    for (const line of integrityText.split('\n')) {
      if (line === '') continue;
      const m = line.match(/^([0-9a-f]{64})  (.+)$/);
      if (!m) { fail(`package.integrity: malformed line '${line.slice(0, 40)}'`); continue; }
      const rel = m[2];
      if (packagePathViolation(rel)) {
        fail(`package.integrity: path violates the package path grammar: ${rel}`);
        continue;
      }
      const normalized = rel.toLowerCase();
      if (seenNormalized.has(normalized)) {
        fail(`package.integrity: case-insensitive path collision: ${rel} vs ${seenNormalized.get(normalized)}`);
        continue;
      }
      seenNormalized.set(normalized, rel);
      listed.set(rel, m[1]);
    }
  } else {
    fail('package.integrity missing');
  }

  const unsigned = { ...manifest, signature: null };
  const canonicalManifestHash = sha256Hex(Buffer.from(canonicalize(unsigned), 'utf8'));
  if (listed.get('manifest.json') !== canonicalManifestHash) {
    fail('manifest.json integrity line does not match its canonicalization (signature=null)');
  }

  for (const rel of listPayloadFiles(pkgDir)) {
    const violation = packagePathViolation(rel);
    if (violation) {
      fail(`payload file violates the package path grammar (${violation}): ${rel}`);
      continue;
    }
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

  let unsignedPackage = signature == null;
  if (signature && integrityText !== null) {
    const contentHash = sha256Hex(fs.readFileSync(integrityPath));
    if (signature.contentHash !== contentHash) {
      fail(`signature.contentHash mismatch (expected ${contentHash})`);
    }

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

    if (manifest.publisher !== fingerprintOf(manifest.publisherPublicKey)) {
      fail('publisher does not equal sha256(raw publisherPublicKey bytes)');
    }
  }

  return { failures, manifest, unsignedPackage };
}
