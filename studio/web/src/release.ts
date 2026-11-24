/**
 * The release the Tenant view's "Bump the release" proposes for a bank's configuration set: the next minor,
 * `a.b.c` to `a.(b+1).0`. A bank's configuration is released as a major or a minor, never a patch, because the
 * rates feed numbers its releases as the patches after the newest (R-11-102, lint L53).
 */
export function nextConfigurationSetRelease(release: unknown): string {
  const [a, b] = String(release ?? '1.0.0').split('.').map((x) => Number.parseInt(x, 10));
  return `${Number.isFinite(a) ? a : 1}.${(Number.isFinite(b) ? b : 0) + 1}.0`;
}
