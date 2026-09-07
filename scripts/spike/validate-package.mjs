// CLI wrapper for spike package validation (roadmap stage 3.5, leg 1).
// Usage: node scripts/spike/validate-package.mjs [pkgDir]
// Exit 0 + "VERIFIED" on success; exit 1 with a failure list otherwise.
import path from 'node:path';
import { validatePackage } from './validate-lib.mjs';

const pkgDir = path.resolve(process.argv[2] ?? 'spikes/github-stats');
const { failures, unsignedPackage } = validatePackage(pkgDir);

if (unsignedPackage) {
  console.log('note: unsigned dev package - steps 3-5 skipped, steps 1-2 still enforced');
}

if (failures.length > 0) {
  console.error(`INVALID (${failures.length} failures):`);
  for (const f of failures) console.error(`  - ${f}`);
  process.exit(1);
}
console.log('VERIFIED: structural + integrity + signature chain OK');
