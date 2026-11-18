// Windows may refuse to load the DTFx harness the studio has just built: Smart App Control, or an
// App Control (WDAC) or AppLocker policy, blocks an unsigned program. The engine then fails on
// every run with the same error. Say so once, plainly, with what to do.

const BLOCKED = /Application Control policy has blocked|0x800711C7|blocked by group policy|0x800704EC/i;

export function blockedByWindows(output: string): string | undefined {
  if (!BLOCKED.test(output)) return undefined;
  return [
    'Windows blocked the DTFx harness this studio built (engines/dtfx/harness/bin/Release/net8.0/DtfxHarness.dll).',
    'An application control policy refuses unsigned programs: Smart App Control on a personal Windows 11 PC, or',
    'App Control or AppLocker set by your organisation. The studio itself is not affected; only running on DTFx is.',
    'Options: ask your IT team to allow that folder; run the studio in WSL or a Linux machine; or, on a personal PC,',
    'turn Smart App Control off (Windows Security, App and browser control). See the user guide, troubleshooting.',
  ].join('\n');
}
