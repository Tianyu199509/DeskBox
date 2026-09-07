// Declarative execution harness (roadmap stage 3.5, leg 1B): the minimal
// host-side loop a runtime:none package needs - permission gate, HOST-
// performed http-json fetch, JSON-path binding evaluation with payload
// fallback, and open-url action resolution. This is the leg-1 counterpart
// the TS-process and WASM legs must reproduce for a fair three-way
// comparison (same behavior: fetch GitHub -> parse -> update -> open repo).
//
// Usage: node scripts/spike/run-declarative.mjs <pkgDir>
//          [--self-test=ok|out-of-scope]   use a local mock server instead
//                                          of the real URL ("ok" also
//                                          grants the mock host so the
//                                          happy path runs; "out-of-scope"
//                                          keeps the original scope so the
//                                          gate must refuse)
//          [--invoke=<actionId>]           resolve an action (prints the
//                                          host shell-open; opens nothing)
//          [--measure]                     include timings + heap in output
// SPIKE-GRADE: the product host owns the real scheduler/renderer; this
// harness exists to make the execution model measurable and testable.
import http from 'node:http';
import path from 'node:path';
import { validatePackage } from './validate-lib.mjs';

const args = process.argv.slice(2);
const pkgDir = path.resolve(args[0] ?? 'spikes/github-stats-live');
const selfTest = args.find(a => a.startsWith('--self-test='))?.slice('--self-test='.length);
const invoke = args.find(a => a.startsWith('--invoke='))?.slice('--invoke='.length);
const measure = args.includes('--measure');

const timings = { validateMs: 0, fetchMs: 0, bindMs: 0 };
let mockServer = null;

main();

async function main() {
  try {
    await run();
  } catch (error) {
    console.error(`FAILED: ${error.message}`);
    process.exitCode = 1;
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
  // The host (not the package) decides whether a governed capability runs.
  // Spike policy: a capability runs when its permission is declared AND the
  // concrete URL host falls inside the declared scope (exact, lowercased).
  const permissions = manifest.permissions ?? [];
  const permissionIds = new Set(permissions.map(p => p.id));
  const allows = id => permissions.find(p => p.id === id)?.scope?.allow ?? [];
  const hostAllowed = (url, permissionId) => {
    try {
      const host = new URL(url).hostname.toLowerCase();
      return permissionIds.has(permissionId) &&
        allows(permissionId).some(entry => entry.toLowerCase() === host);
    } catch {
      return false;
    }
  };

  const dataSources = manifest.dataSources ?? {};
  const actions = manifest.actions ?? {};

  // ---------- data sources: the host performs the fetch ----------
  const fetched = {};
  const t1 = performance.now();
  for (const [id, source] of Object.entries(dataSources)) {
    if (source.type !== 'http-json') continue;
    let url = source.url;
    if (selfTest === 'ok') {
      url = await startMockAndGrant(source.url, permissions);
    } else if (selfTest === 'out-of-scope') {
      url = await startMockOnly(source.url);
    }

    if (!hostAllowed(url, 'network.fetch')) {
      console.error(
        `REFUSED: dataSources['${id}'] url '${url}' is outside the declared network.fetch scope`);
      process.exitCode = 1;
      return;
    }

    const response = await fetch(url, {
      headers: { 'User-Agent': 'DeskBox-spike-leg1B', Accept: 'application/json' },
      signal: AbortSignal.timeout(10_000)
    });
    if (!response.ok) {
      throw new Error(`fetch ${url} -> HTTP ${response.status}`);
    }
    fetched[id] = await response.json();
  }
  timings.fetchMs = Math.round(performance.now() - t1);

  // ---------- bindings: JSON-path overrides with payload fallback ----------
  const t2 = performance.now();
  const widgetStates = {};
  for (const contribution of manifest.contributions) {
    const state = { ...(contribution.payload ?? {}) };
    const bound = {};
    for (const [field, binding] of Object.entries(contribution.bindings ?? {})) {
      const value = evaluateJsonPath(fetched[binding.source], binding.path);
      if (value !== undefined) {
        state[field] = value;
        bound[field] = true;
      } else {
        // Failed fetch or missing path: the payload fallback value stays.
        bound[field] = 'fallback';
      }
    }
    widgetStates[contribution.id] = { state, bound };
  }
  timings.bindMs = Math.round(performance.now() - t2);

  // ---------- action resolution (host shell-open; opens nothing here) ----------
  let invocation = null;
  if (invoke !== undefined) {
    const action = actions[invoke];
    if (!action) {
      console.error(`REFUSED: unknown action '${invoke}'`);
      process.exitCode = 1;
      return;
    }
    if (!hostAllowed(action.url, 'shell.open')) {
      console.error(
        `REFUSED: actions['${invoke}'] url '${action.url}' is outside the declared shell.open scope`);
      process.exitCode = 1;
      return;
    }
    invocation = { actionId: invoke, type: action.type, url: action.url };
  }

  const output = { package: manifest.id, widgetStates, invocation };
  if (measure) {
    output.measurements = { ...timings, heapUsedKb: Math.round(process.memoryUsage().heapUsed / 1024) };
  }
  console.log(JSON.stringify(output, null, 2));
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

function startMockServer() {
  return new Promise(resolve => {
    const payload = { stargazers_count: 1284, full_name: 'Tianyu199509/DeskBox' };
    const server = http.createServer((req, res) => {
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify(payload));
    });
    server.listen(0, '127.0.0.1', () => resolve(server));
  });
}

async function startMockAndGrant(originalUrl, permissions) {
  mockServer = await startMockServer();
  const port = mockServer.address().port;
  // Grant the mock host: same treatment the real host's install-time scope
  // check would have given the production URL. Scopes are hostnames; the
  // ephemeral mock port is irrelevant to the permission model.
  permissions.find(p => p.id === 'network.fetch').scope.allow.push('127.0.0.1');
  return `http://127.0.0.1:${port}${new URL(originalUrl).pathname}`;
}

async function startMockOnly(originalUrl) {
  mockServer = await startMockServer();
  const port = mockServer.address().port;
  return `http://127.0.0.1:${port}${new URL(originalUrl).pathname}`;
}
