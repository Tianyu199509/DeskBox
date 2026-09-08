// Leg-2 spike host harness (roadmap stage 3.5): external TS/JS process
// runtime. The plugin is THIRD-PARTY CODE in its own process; the host
// spawns it, talks newline-delimited JSON-RPC over stdio, and serves every
// capability call through the SAME gate as the declarative leg (requested
// AND in-scope AND granted, redirects refused, 2MB cap) - process isolation
// plus host-side enforcement, NOT trust.
//
// Process governance (ThumbnailProxy pattern, spike-grade): overall
// deadline with kill, stderr capture, exit-code propagation.
//
// Usage: node scripts/spike/run-process.mjs <pkgDir>
//          [--grant=<permissionId>=<host>]...
//          [--self-test=ok|redirect|server-error|evil-ask|crash]
//               ok / redirect / server-error: plugin fetches are routed to
//                   a local mock (manifest scope rewritten for the mock
//                   host; grants stay external, exactly like leg 1B)
//               evil-ask: the plugin's requested URL is rewritten to an
//                   out-of-scope host - the gate must refuse the CALL and
//                   the plugin must degrade to fallback (hostile-code test)
//               crash: the plugin process exits abruptly (governance test)
//          [--invoke-widget=<contributionId>]  resolve payload.primaryActionId
//          [--measure]  spawn + activation timings + host heap
import http from 'node:http';
import { spawn } from 'node:child_process';
import path from 'node:path';
import readline from 'node:readline';
import { validatePackage } from './validate-lib.mjs';
import { PolicyRefused, createCapabilityGate, fetchHttpJson } from './host-capabilities.mjs';

const OVERALL_DEADLINE_MS = 15_000;
const SETTLE_GRACE_MS = 250;

const args = process.argv.slice(2);
const pkgDir = path.resolve(args[0] ?? 'spikes/github-stats-process');
const selfTest = args.find(a => a.startsWith('--self-test='))?.slice('--self-test='.length);
const invokeWidget = args.find(a => a.startsWith('--invoke-widget='))?.slice('--invoke-widget='.length);
const measure = args.includes('--measure');
const grants = args.filter(a => a.startsWith('--grant=')).map(a => a.slice('--grant='.length));

const timings = { spawnMs: 0, activateMs: 0 };

main();

async function main() {
  let mockServer = null;
  try {
    const output = await run();
    if (output !== null) {
      console.log(JSON.stringify(output, null, 2));
    }
  } catch (error) {
    console.error(`${error instanceof PolicyRefused ? 'REFUSED' : 'FAILED'}: ${error.message}`);
    process.exitCode = 1;
  } finally {
    if (mockServer !== null) {
      mockServerHolder.server?.close();
      mockServerHolder.server?.unref();
    }
    void mockServer;
    // Node's fetch stack (undici keep-alive sockets) can hold the event
    // loop open after the work is done; a CLI harness exits explicitly.
    process.exit(process.exitCode ?? 0);
  }
}

const mockServerHolder = { server: null };
let mockFetchUrl = null;

async function run() {
  const { failures, manifest } = validatePackage(pkgDir);
  if (failures.length > 0) {
    console.error(`INVALID (${failures.length} failures):`);
    for (const f of failures) console.error(`  - ${f}`);
    process.exitCode = 1;
    return null;
  }
  if (manifest.runtime !== 'process' || typeof manifest.entry?.main !== 'string') {
    throw new Error('package is not a process plugin (runtime "process" + entry.main required)');
  }

  const permissions = manifest.permissions ?? [];
  const selfTestUsesMock = ['ok', 'redirect', 'server-error'].includes(selfTest);
  const gate = createCapabilityGate(permissions, grants, {
    allowInsecureLoopback: selfTestUsesMock
  });

  if (selfTest === 'ok' || selfTest === 'redirect' || selfTest === 'server-error') {
    const server = await startMockServer(selfTest);
    mockServerHolder.server = server;
    const port = server.address().port;
    permissions.find(p => p.id === 'network.fetch').scope.allow.push('127.0.0.1');
    mockFetchUrl = `http://127.0.0.1:${port}/repos/Tianyu199509/DeskBox`;
  }

  // ---------- spawn + govern ----------
  const pluginPath = path.join(pkgDir, manifest.entry.main);
  const spawnStart = performance.now();
  const childEnv = selfTest === 'crash'
    ? { ...process.env, DESKBOX_SPIKE_CRASH: '1' }
    : process.env;
  const child = spawn(process.execPath, [pluginPath], { stdio: ['pipe', 'pipe', 'pipe'], env: childEnv });
  timings.spawnMs = Math.round(performance.now() - spawnStart);

  child.stderr.on('data', chunk => {
    const text = chunk.toString().trim();
    if (text) console.error(`[plugin stderr] ${text}`);
  });

  const session = {
    widgetStates: {},
    capabilityCalls: [],
    refusals: [],
    invocation: null,
    firstUpdateMs: null,
    shellCallSeen: false
  };
  const activateStart = { at: 0 };

  const done = new Promise((resolve, reject) => {
    const killer = setTimeout(() => {
      reject(new Error(`plugin did not finish within ${OVERALL_DEADLINE_MS}ms (killed)`));
      try { child.kill(); } catch {}
    }, OVERALL_DEADLINE_MS);

    child.on('error', error => {
      clearTimeout(killer);
      reject(error);
    });
    child.on('exit', (code, signal) => {
      clearTimeout(killer);
      if (session.firstUpdateMs === null) {
        reject(new Error(
          `plugin process exited with code ${code ?? 'null'}${signal ? ` (${signal})` : ''} before producing widget state`));
        return;
      }
      if ((code ?? 0) !== 0) {
        // A crash after the first state push is still a crash - process
        // governance must surface it, not swallow it as success.
        reject(new Error(
          `plugin process crashed after producing state (exit code ${code ?? 'null'}${signal ? `, ${signal}` : ''})`));
        return;
      }
      resolve(session);
    });

    const rl = readline.createInterface({ input: child.stdout });
    rl.on('line', line => {
      if (line.trim() === '') return;
      let message;
      try {
        message = JSON.parse(line);
      } catch {
        return;
      }
      void handlePluginMessage(child, message, session, gate, activateStart);
    });
  });

  // ---------- activation ----------
  activateStart.at = performance.now();
  child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', method: 'activate', params: { reason: 'startup' } })}\n`);

  let wantActionId = null;
  if (invokeWidget !== undefined) {
    const contribution = manifest.contributions.find(c => c.id === invokeWidget);
    const actionId = contribution?.payload?.primaryActionId;
    if (typeof actionId !== 'string') {
      throw new Error(`widget '${invokeWidget}' declares no primaryActionId`);
    }
    wantActionId = actionId;
    setTimeout(() => {
      try {
        child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', method: 'action.invoke', params: { actionId } })}\n`);
      } catch {}
    }, 150);
  }

  // Wait for the first state push (and the action call when requested);
  // race against the plugin's death so an early crash surfaces immediately
  // instead of waiting for the state timeout.
  await Promise.race([
    waitUntil(
      () => session.firstUpdateMs !== null && (wantActionId === null || session.shellCallSeen),
      OVERALL_DEADLINE_MS,
      'plugin did not produce widget state in time'),
    done
  ]);
  timings.activateMs = session.firstUpdateMs ?? 0;
  await sleep(SETTLE_GRACE_MS);
  try { child.stdin.end(); } catch {}

  await done;

  return {
    package: manifest.id,
    widgetStates: session.widgetStates,
    invocation: session.invocation,
    capabilityCalls: session.capabilityCalls,
    refusals: session.refusals.length > 0 ? session.refusals : undefined,
    ...(measure ? { measurements: { ...timings, heapUsedKb: Math.round(process.memoryUsage().heapUsed / 1024) } } : {})
  };
}

async function handlePluginMessage(child, message, session, gate, activateStart) {
  // Capability call (plugin -> host request): gate host-side, then execute.
  if (message.id !== undefined && typeof message.method === 'string') {
    session.capabilityCalls.push({ method: message.method, params: message.params });
    try {
      if (message.method === 'network.fetch') {
        let url = message.params?.url ?? '';
        if (mockFetchUrl !== null) {
          url = mockFetchUrl;
        } else if (selfTest === 'evil-ask') {
          url = 'https://evil.example/secret';
        } else if (selfTest === 'downgrade') {
          // Same host, wrong scheme: the gate must refuse http even when
          // the host is in scope AND granted (round-8 scheme enforcement).
          url = url.replace('https://', 'http://');
        }
        gate.requireAllowed(url, 'network.fetch');
        const result = await fetchHttpJson(url);
        reply(child, message.id, { result });
      } else if (message.method === 'shell.open') {
        const url = message.params?.url ?? '';
        gate.requireAllowed(url, 'shell.open');
        session.shellCallSeen = true;
        session.invocation = { type: 'open-url', url, note: 'spike harness prints intent' };
        reply(child, message.id, { result: { opened: false } });
      } else {
        reply(child, message.id, { error: { code: -32601, message: `unknown capability ${message.method}` } });
      }
    } catch (error) {
      if (error instanceof PolicyRefused) {
        session.refusals.push({ method: message.method, message: error.message });
      }
      reply(child, message.id, { error: { code: -32000, message: error.message } });
    }
    return;
  }

  // State push (plugin -> host notification).
  if (message.method === 'widget.update') {
    session.widgetStates[message.params?.contributionId] = {
      state: message.params?.state,
      note: message.params?.note
    };
    if (session.firstUpdateMs === null) {
      session.firstUpdateMs = Math.round(performance.now() - activateStart.at);
    }
  }
}

function reply(child, id, payload) {
  child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', id, ...payload })}\n`);
}

function waitUntil(predicate, timeoutMs, timeoutMessage) {
  return new Promise((resolve, reject) => {
    const started = performance.now();
    const timer = setInterval(() => {
      if (predicate()) {
        clearInterval(timer);
        resolve();
      } else if (performance.now() - started > timeoutMs) {
        clearInterval(timer);
        reject(new Error(timeoutMessage));
      }
    }, 25);
  });
}

function sleep(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
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
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ stargazers_count: 1284, full_name: 'Tianyu199509/DeskBox' }));
    });
    server.listen(0, '127.0.0.1', () => resolve(server));
  });
}
