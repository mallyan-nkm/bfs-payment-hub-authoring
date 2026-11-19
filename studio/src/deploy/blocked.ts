// Windows may refuse to load an engine harness the studio has just built (DTFx or Elsa): Smart App Control, or an
// App Control (WDAC) or AppLocker policy, blocks an unsigned program. The engine then fails on
// every run with the same error. Say so once, plainly, with what to do.

const BLOCKED = /Application Control policy has blocked|0x800711C7|blocked by group policy|0x800704EC/i;

export function blockedByWindows(output: string, engine: 'dtfx' | 'elsa' = 'dtfx'): string | undefined {
  const name = engine === 'elsa' ? 'Elsa' : 'DTFx';
  const dll = engine === 'elsa' ? 'engines/elsa/harness/bin/Release/net8.0/ElsaHarness.dll' : 'engines/dtfx/harness/bin/Release/net8.0/DtfxHarness.dll';
  if (!BLOCKED.test(output)) return undefined;
  return [
    `Windows blocked the ${name} harness this studio built (${dll}).`,
    'An application control policy refuses unsigned programs: Smart App Control on a personal Windows 11 PC, or',
    `App Control or AppLocker set by your organisation. The studio itself is not affected; only running on ${name} is.`,
    'Options: ask your IT team to allow that folder; run the studio in WSL or a Linux machine; or, on a personal PC,',
    'turn Smart App Control off (Windows Security, App and browser control). See the user guide, troubleshooting.',
  ].join('\n');
}
