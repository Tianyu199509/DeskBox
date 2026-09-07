// Shared host-capability machinery for the spike legs (roadmap stage 3.5):
// the permission gate and the hardened fetcher. Both the declarative leg
// (run-declarative.mjs) and the process leg (run-process.mjs) must enforce
// IDENTICAL semantics - requested (manifest) AND url-in-declared-scope AND
// granted (host-side), redirects refused, response size capped - so the
// three-way comparison measures runtimes, not policy drift.
export class PolicyRefused extends Error {}

export const MAX_RESPONSE_BYTES = 2 * 1024 * 1024; // host hard limit, packages cannot raise it

// permissions: the manifest's REQUESTED permission list (mutated by
// self-test modes to scope the mock host). grants: host-side grant
// strings like "network.fetch=api.github.com".
export function createCapabilityGate(permissions, grants) {
  const permissionIds = new Set(permissions.map(p => p.id));
  const allows = id => permissions.find(p => p.id === id)?.scope?.allow ?? [];
  const grantedHosts = id =>
    grants.filter(g => g.startsWith(`${id}=`)).map(g => g.slice(id.length + 1).toLowerCase());

  return {
    requireAllowed(url, permissionId) {
      if (!permissionIds.has(permissionId)) {
        throw new PolicyRefused(`capability '${permissionId}' is not declared by the package`);
      }
      const host = new URL(url).hostname.toLowerCase();
      if (!allows(permissionId).some(entry => entry.toLowerCase() === host)) {
        throw new PolicyRefused(`url '${url}' is outside the declared ${permissionId} scope`);
      }
      if (!grantedHosts(permissionId).includes(host)) {
        throw new PolicyRefused(
          `url host '${host}' has no granted ${permissionId} capability (requested != granted)`);
      }
      return true;
    }
  };
}

// Hardened host-side fetch: redirects are refused (a 3xx would move the
// request to a host that never passed the gate), the body is streamed with
// a hard byte cap, and the JSON must parse. All failures are data failures
// (thrown as plain Errors) EXCEPT redirect attempts, which are policy
// refusals.
export async function fetchHttpJson(url) {
  const response = await fetch(url, {
    headers: { 'User-Agent': 'DeskBox-spike', Accept: 'application/json' },
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
    return { json: JSON.parse(text) };
  } catch {
    throw new Error('response is not valid JSON');
  }
}
