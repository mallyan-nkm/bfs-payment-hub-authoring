// The next release of a document the studio's editors bump (R-11-36). A bank's configuration is released as a major or a minor,
// never a patch: the rates feed numbers its releases as the patches after the bank's newest (lint L53, R-11-102). So a configuration
// set's bump raises the minor; a document whose patches are allowed raises the patch.

/** Whether a document of this kind may be released as a patch. */
export function patchAllowed(kind: string | undefined): boolean {
  return kind !== 'configuration-set';
}

/** The release after `release`: the next minor (x.y+1.0) where patches are not allowed, else the next patch. */
export function nextRelease(release: string | undefined, patches: boolean): string {
  const [a, b, c] = String(release ?? '1.0.0').split('.').map((x) => Number(x) || 0);
  return patches ? `${a}.${b}.${(c ?? 0) + 1}` : `${a}.${(b ?? 0) + 1}.0`;
}
