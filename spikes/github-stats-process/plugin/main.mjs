// Leg-2 spike plugin (roadmap stage 3.5): a TypeScript/JS external process.
// Same behavior as the declarative leg - fetch the DeskBox GitHub repo,
// bind stargazers_count into the metric widget, open the repo on action -
// but here the logic is THIRD-PARTY CODE: the host never lets it touch the
// network or shell directly; every capability goes through a host-side
// capability call gated by requested ∧ in-scope ∧ granted permissions.
//
// Transport: newline-delimited JSON-RPC 2.0 over stdio (the spike's framing
// choice; LSP-style headers are a later protocol decision).
//
// Host -> plugin: {"method":"activate"|"action.invoke", ...} (requests)
// Plugin -> host: {"id":N,"method":"network.fetch"|"shell.open", ...}
//                (capability calls) and {"method":"widget.update", ...}
//                (state pushes).
import readline from 'node:readline';

const REPO_URL = 'https://api.github.com/repos/Tianyu199509/DeskBox';
const REPO_PAGE = 'https://github.com/Tianyu199509/DeskBox';
const FALLBACK_PAYLOAD = {
  version: 1,
  label: 'Stars',
  value: '…',
  caption: 'Tianyu199509/DeskBox',
  primaryActionId: 'open-repo'
};

let nextRequestId = 1;
const pending = new Map();

function send(message) {
  process.stdout.write(`${JSON.stringify(message)}\n`);
}

function callCapability(method, params) {
  return new Promise((resolve, reject) => {
    const id = nextRequestId++;
    pending.set(id, { resolve, reject });
    send({ jsonrpc: '2.0', id, method, params });
  });
}

function pushWidgetState(state, note) {
  send({
    jsonrpc: '2.0',
    method: 'widget.update',
    params: { contributionId: 'live-stars', state, note }
  });
}

async function activate() {
  // The plugin never fetches directly - it ASKS the host, and the host
  // applies the permission gate (declared + scope + grant) host-side.
  try {
    const response = await callCapability('network.fetch', { url: REPO_URL });
    const stars = response?.json?.stargazers_count;
    if (typeof stars !== 'number') {
      throw new Error('stargazers_count missing from response');
    }
    pushWidgetState({ ...FALLBACK_PAYLOAD, value: stars });
  } catch (error) {
    // Capability refusal or data failure: degrade to the fallback payload
    // and keep rendering - same resilience contract as the declarative leg.
    pushWidgetState(FALLBACK_PAYLOAD, `fallback (${error.message})`);
  }
}

async function invokeAction(actionId) {
  if (actionId !== 'open-repo') {
    send({ jsonrpc: '2.0', id: -1, error: { code: -32601, message: `unknown action ${actionId}` } });
    return;
  }
  try {
    await callCapability('shell.open', { url: REPO_PAGE });
  } catch {
    // Refused host-side; the widget stays alive.
  }
}

const rl = readline.createInterface({ input: process.stdin });
rl.on('line', line => {
  if (line.trim() === '') return;
  let message;
  try {
    message = JSON.parse(line);
  } catch {
    return;
  }
  if (message.method === 'activate') {
    void activate();
  } else if (message.method === 'action.invoke') {
    void invokeAction(message.params?.actionId);
  } else if (message.id !== undefined && pending.has(message.id)) {
    const waiter = pending.get(message.id);
    pending.delete(message.id);
    if (message.error) {
      waiter.reject(new Error(message.error.message ?? 'capability error'));
    } else {
      waiter.resolve(message.result);
    }
  }
});

// Crash-mode hook for the process-governance test.
if (process.env.DESKBOX_SPIKE_CRASH === '1') {
  setTimeout(() => process.exit(3), 30);
}
