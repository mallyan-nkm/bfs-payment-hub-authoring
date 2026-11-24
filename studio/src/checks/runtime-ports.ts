// A port the bench gives to the runtime (simulator `runtime`, R-08-15) is answered by the bank's port binding through the runtime's
// native host, so every activity that calls it must have a native code-behind there (Issue I-268, R-09-213): one that has none would
// find no simulator in the bench and fall back to the port's first simulator. A composite's subflow calls the activities that do the
// work, so a composite is not counted.

export interface BenchEntry { port: string; simulator: string; scenario?: string }

/** The activities of each port the bench gives to the runtime that have no native code-behind, by name and release. */
export function runtimePortGaps(activities: Iterable<any>, bench: BenchEntry[] | undefined): { port: string; activity: string; release: string }[] {
  const ports = new Set((bench ?? []).filter((b) => b.simulator === 'runtime').map((b) => b.port));
  if (!ports.size) return [];
  const latest = new Map<string, any>();
  for (const a of activities) {
    if (a?.kind !== 'activity' || !a.name) continue;
    const had = latest.get(a.name);
    if (!had || compare(a.release ?? '0.0.0', had.release ?? '0.0.0') > 0) latest.set(a.name, a);
  }
  const out: { port: string; activity: string; release: string }[] = [];
  for (const a of latest.values()) {
    if (a.implementation?.binding === 'subflow' || a.implementation?.native === true) continue;
    for (const p of a.ports ?? []) if (ports.has(p)) out.push({ port: p, activity: a.name, release: a.release ?? '' });
  }
  return out.sort((x, y) => (x.activity < y.activity ? -1 : x.activity > y.activity ? 1 : 0));
}

function compare(a: string, b: string): number {
  const pa = a.split('.').map(Number);
  const pb = b.split('.').map(Number);
  for (let i = 0; i < 3; i++) if ((pa[i] ?? 0) !== (pb[i] ?? 0)) return (pa[i] ?? 0) - (pb[i] ?? 0);
  return 0;
}
