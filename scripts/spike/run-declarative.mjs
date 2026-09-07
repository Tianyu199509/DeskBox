// Declarative execution harness (roadmap stage 3.5, leg 1B): the minimal
// host-side loop a runtime:none package needs - the requested/granted
// permission split, HOST-performed http-json fetch (no redirects, size
// capped), JSON-path binding evaluation with payload fallback, and
// open-url action resolution. This is the leg-1 counterpart the TS-process
// and WASM legs must reproduce for a fair three-way comparison (same
// behavior: fetch GitHub -> parse -> update -> open repo) under the SAME
// permission semantics.
//
// Permission model (round 7): the manifest carries REQUESTED permissions;
// grants come from the host side only (--grant id=host, repeatable; the
// product host persists the install-time user/policy decision). A
// capability runs only when requested AND in-manifest-scope AND granted.
// No grants => everything is refused (fail closed).
//
// Error model (round 7): policy failures (undeclared permission, host
// outside scope, no grant, redirect) REFUSE with exit 1 BEFORE any bytes
// move. Data failures (offline, timeout, HTTP 5xx, oversized body, JSON
// parse) mark the data source as failed; its bindings keep the payload
// fallback and the run still produces widget state (exit 0).
//
// Usage: node scripts/spike/run-declarative.mjs <pkgDir>
//          [--grant <permissionId>=<host>]...      host-side grant set
//          [--self-test=ok|out-of-scope|redirect|server-error|huge]
//                                               local mock server modes
//                                               ("ok" rewrites the
//                                               manifest scope for the
//                                               mock host; grants still
//                                               come from --grant)
//          [--invoke-widget=<contributionId>]    resolve the widget's
//                                               primaryActionId
//          [--invoke=<actionId>]                 resolve a root action
//          [--measure]                           timings + heap in output
// SPIKE-GRADE: the product host owns the real scheduler/renderer.
import http from 'node:http';
import path from 'node:path';
import { validatePackage } from './validate-lib.mjs';

const MAX_RESPONSE_BYTES = 2 * 1024 * 1024; // host hard limit, packages cannot raise it

const args = process.argv.slice(2);
const pkgDir = path.resolve(args[0] ?? 'spikes/github-stats-live');
const selfTest = args.find(a => a.startsWith('--self-test='))?.slice('--self-test='.length);
const invoke = args.find(a => a.startsWith('--invoke='))?.slice('--invoke='.length);
const invokeWidget = args.find(a => a.startsWith('--invoke-widget='))?.slice('--invoke-widget='.length);
const measure = args.includes('--measure');
const grants = args
  .filter(a => a.startsWith('--grant='))
  .map(a => a.slice('--grant='.length));

class PolicyRefused extends Error {}

const timings = { validateMs: 0, fetchMs: 0, bindMs: 0 };
let mockServer = null;

main();

async function main() {
  try {
    await run();
  } catch (error) {
    if (error instanceof PolicyRefused) {
      console.error(`REFUSED: ${error.message}`);
      process.exitCode = 1;
    } else {
      console.error(`FAILED: ${error.message}`);
      process.exitCode = 1;
    }
  } finally {
    if (mockServer) {
      mockServer.close();
      mockServer.unref();
    }
  }
}

async function run() {
  // ---------- package validation ----------
  const t0 = performance.now();
  const { failures, manifest } = validatePackage(pkgDir);
  timings.validateMs = Math.round(performance.now() - t0);
  if (failures.length > 0) {
    console.error(`INVALID (${failures.length} failures):`);
    for (const f of failures) console.error(`  - ${f}`);
    process.exitCode = 1;
    return;
  }

  // ---------- host policy gate ----------
  // Requested (manifest) vs granted (host). A capability needs all three:
  // declared permission + URL host inside the declared scope + a grant.
  const permissions = manifest.permissions ?? [];
  const permissionIds = new Set(permissions.map(p => p.id));
  const allows = id => permissions.find(p => p.id === id)?.scope?.allow ?? [];
  const grantedHosts = id =>
    grants.filter(g => g.startsWith(`${id}=`)).map(g => g.slice(id.length + 1).toLowerCase());
  const hostname = url => new URL(url).hostname.toLowerCase();

  const hostAllowed = (url, permissionId) => {
    if (!permissionIds.has(permissionId)) {
      throw new PolicyRefused(`capability '${permissionId}' is not declared by the package`);
    }
    const host = hostname(url);
    if (!allows(permissionId).some(entry => entry.toLowerCase() === host)) {
      throw new PolicyRefused(
        `url '${url}' is outside the declared ${permissionId} scope`);
    }
    if (!grantedHosts(permissionId).includes(host)) {
      throw new PolicyRefused(
        `url host '${host}' has no granted ${permissionId} capability (requested != granted)`);
    }
    return true;
  };

  const dataSources = manifest.dataSources ?? {};
  const actions = manifest.actions ?? {};

  // ---------- data sources: the host performs the fetch ----------
  const fetched = {};
  const dataSourceErrors = {};
  const t1 = performance.now();
  for (const [id, source] of Object.entries(dataSources)) {
    if (source.type !== 'http-json') continue;
    let url = source.url;
    if (selfTest === 'ok' || selfTest === 'redirect' || selfTest === 'server-error' || selfTest === 'huge') {
      // The manifest scope is rewritten for the mock host (same treatment
      // the install-time check would have given the production host);
      // grants are NOT auto-added - they must come from --grant.
      url = await startMockAndRewriteScope(source.url, selfTest, permissions);
    } else if (selfTest === 'out-of-scope') {
      url = await startMockOnly(source.url);
    }

    hostAllowed(url, 'network.fetch'); // policy failures refuse before any bytes move

    try {
      fetched[id] = await fetchHttpJson(url);
    } catch (error) {
      if (error instanceof PolicyRefused) {
        throw error; // redirect-attempt is a policy refusal, not a data failure
      }
      // Data failure: the source is marked failed and its bindings fall
      // back to the payload values; the widget keeps rendering.
      dataSourceErrors[id] = error.message;
    }
  }
  timings.fetchMs = Math.round(performance.now() - t1);

  // ---------- bindings: JSON-path overrides with payload fallback ----------
  const t2 = performance.now();
  const widgetStates = {};
  for (const contribution of manifest.contributions) {
    const state = { ...(contribution.payload ?? {}) };
    const bound = {};
    for (const [field, binding] of Object.entries(contribution.bindings ?? {})) {
      const failed = dataSourceErrors[binding.source] !== undefined;
      const value = failed ? undefined : evaluateJsonPath(fetched[binding.source], binding.path);
      if (value !== undefined) {
        state[field] = value;
        bound[field] = true;
      } else {
        bound[field] = failed ? `fallback (${dataSourceErrors[binding.source]})` : 'fallback';
      }
    }
    widgetStates[contribution.id] = { state, bound };
  }
  timings.bindMs = Math.round(performance.now() - t2);

  // ---------- action resolution (host shell-open; opens nothing here) ----------
  let invocation = null;
  if (invokeWidget !== undefined) {
    const contribution = manifest.contributions.find(c => c.id === invokeWidget);
    if (!contribution) {
      console.error(`REFUSED: unknown widget '${invokeWidget}'`);
      process.exitCode = 1;
      return;
    }
    const actionId = contribution.payload?.primaryActionId;
    if (typeof actionId !== 'string') {
      console.error(`REFUSED: widget '${invokeWidget}' declares no primaryActionId`);
      process.exitCode = 1;
      return;
    }
    invocation = resolveAction(actionId, actions, hostAllowed);
    if (invocation === null) {
      process.exitCode = 1;
      return;
    }
  } else if (invoke !== undefined) {
    invocation = resolveAction(invoke, actions, hostAllowed);
    if (invocation === null) {
      process.exitCode = 1;
      return;
    }
  }

  const output = {
    package: manifest.id,
    widgetStates,
    invocation,
    dataSourceErrors: Object.keys(dataSourceErrors).length > 0 ? dataSourceErrors : undefined
  };
  if (measure) {
    output.measurements = { ...timings, heapUsedKb: Math.round(process.memoryUsage().heapUsed / 1024) };
  }
  console.log(JSON.stringify(output, null, 2));
}

function resolveAction(actionId, actions, hostAllowed) {
  const action = actions[actionId];
  if (!action) {
    console.error(`REFUSED: unknown action '${actionId}'`);
    return null;
  }
  hostAllowed(action.url, 'shell.open');
  return { actionId, type: action.type, url: action.url };
}

async function fetchHttpJson(url) {
  // Redirects are NOT followed (v0.3): a 3xx would move the request to a
  // host that never passed the capability gate. Refuse instead.
  const response = await fetch(url, {
    headers: { 'User-Agent': 'DeskBox-spike-leg1B', Accept: 'application/json' },
    redirect: 'manual',
    signal: AbortSignal.timeout(10_000)
  });
  if (response.type === 'opaqueredirect' || (response.status >= 300 && response.status < 400)) {
    throw new PolicyRefused(
      `url '${url}' attempted a redirect; redirects are refused in v0.3 instead of followed`);
  }
  if (!response.ok) {
    throw new Error(`HTTP ${response.status}`);
  }

  const declaredLength = Number(response.headers.get('content-length') ?? 0);
  if (declaredLength > MAX_RESPONSE_BYTES) {
    throw new Error(`response exceeds the ${MAX_RESPONSE_BYTES}-byte host limit (${declaredLength} declared)`);
  }

  // Stream the body with a hard cap so a hostile server cannot balloon
  // memory with an unbounded "JSON" payload.
  const reader = response.body.getReader();
  const chunks = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > MAX_RESPONSE_BYTES) {
      await reader.cancel();
      throw new Error(`response exceeds the ${MAX_RESPONSE_BYTES}-byte host limit`);
    }
    chunks.push(value);
  }
  const text = Buffer.concat(chunks).toString('utf8');
  try {
    return JSON.parse(text);
  } catch {
    throw new Error('response is not valid JSON');
  }
}

function evaluateJsonPath(value, pathExpression) {
  if (value === undefined || value === null) return undefined;
  if (!/^\$\.[A-Za-z0-9_\[\].]*$/.test(pathExpression)) return undefined;
  let current = value;
  for (const segment of pathExpression.slice(2).split('.')) {
    const m = segment.match(/^([A-Za-z0-9_]+)((?:\[\d+\])*)$/);
    if (!m) return undefined;
    if (m[1] !== '') current = current?.[m[1]];
    for (const index of m[2].match(/\[\d+\]/g) ?? []) {
      current = current?.[Number(index.slice(1, -1))];
    }
    if (current === undefined || current === null) return undefined;
  }
  return current;
}

function startMockServer(mode) {
  return new Promise(resolve => {
    const server = http.createServer((req, res) => {
      if (mode === 'redirect') {
        res.writeHead(302, { Location: 'https://evil.example/secret' });
        res.end();
        return;
      }
      if (mode === 'server-error') {
        res.writeHead(500, { 'Content-Type': 'text/plain' });
        res.end('boom');
        return;
      }
      if (mode === 'huge') {
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(`{"padding":"${'x'.repeat(3 * 1024 * 1024)}","stargazers_count":1284}`);
        return;
      }
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ stargazers_count: 1284, full_name: 'Tianyu199509/DeskBox' }));
    });
    server.listen(0, '127.0.0.1', () => resolve(server));
  });
}

async function startMockAndRewriteScope(originalUrl, mode, permissions) {
  mockServer = await startMockServer(mode);
  const port = mockServer.address().port;
  // Scopes are hostnames; the ephemeral mock port is irrelevant.
  permissions.find(p => p.id === 'network.fetch').scope.allow.push('127.0.0.1');
  return `http://127.0.0.1:${port}${new URL(originalUrl).pathname}`;
}

async function startMockOnly(originalUrl) {
  mockServer = await startMockServer('ok');
  const port = mockServer.address().port;
  return `http://127.0.0.1:${port}${new URL(originalUrl).pathname}`;
}
