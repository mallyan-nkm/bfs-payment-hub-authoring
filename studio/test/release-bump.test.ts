import { describe, expect, it } from 'vitest';
import { nextRelease, patchAllowed } from '../web/src/release.js';

// The configuration set editor's "Bump the release" (R-11-102, lint L53): a bank's configuration is a major or a minor, never a patch.
describe("bumping a release in the studio's editors", () => {
  it("raises the minor of a bank's configuration set, which L53 takes", () => {
    expect(patchAllowed('configuration-set')).toBe(false);
    expect(nextRelease('1.10.0', patchAllowed('configuration-set'))).toBe('1.11.0');
    expect(nextRelease('1.9.3', false)).toBe('1.10.0');
    expect(nextRelease(undefined, false)).toBe('1.1.0');
  });

  it('keeps the patch where patches are allowed', () => {
    expect(patchAllowed('rule')).toBe(true);
    expect(nextRelease('1.2.0', patchAllowed('rule'))).toBe('1.2.1');
  });
});
