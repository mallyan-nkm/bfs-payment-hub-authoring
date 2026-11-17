import * as fs from 'node:fs';
import * as path from 'node:path';
import { parseYaml } from './yaml.js';

// Code-set registries (C03-S5, R-02-11). Versioned; a lookup reads the release in force on a date.
// An unknown code is an error that names the registry version.

export class RegistryError extends Error {
  constructor(public set: string, public code: string, public version: string) {
    super(`${code} is not in ${set}, registry version ${version}`);
  }
}

export interface RegistryRelease { version: string; validFrom: string; validTo?: string; provenance?: any; sets: Record<string, Record<string, string>> }

export class CodeRegistry {
  constructor(readonly releases: RegistryRelease[]) {}

  static load(dir: string): CodeRegistry {
    const out: RegistryRelease[] = [];
    if (fs.existsSync(dir)) for (const f of fs.readdirSync(dir).filter((f) => f.endsWith('.yaml')).sort()) out.push(...(parseYaml(fs.readFileSync(path.join(dir, f), 'utf8')).releases ?? []));
    return new CodeRegistry(out);
  }

  at(date: string): RegistryRelease {
    const r = this.releases.filter((x) => x.validFrom <= date && (!x.validTo || x.validTo >= date)).sort((a, b) => (a.validFrom < b.validFrom ? 1 : -1))[0];
    if (!r) throw new Error(`No code registry is in force on ${date}`);
    return r;
  }

  lookup(set: string, code: string, date: string): { code: string; name: string; version: string } {
    const r = this.at(date);
    const name = r.sets[set]?.[code];
    if (name === undefined) throw new RegistryError(set, code, r.version);
    return { code, name, version: r.version };
  }

  /** Every reason code a factory rule or simulator uses, found or missing, in one report (C03-S5). */
  reasonCodeReport(docs: any[], date: string) {
    const used = new Map<string, Set<string>>();
    const add = (c: string, by: string) => {
      if (!used.has(c)) used.set(c, new Set());
      used.get(c)!.add(by);
    };
    for (const d of docs) {
      if (d.kind === 'rule') for (const t of d.then ?? []) if (t.yield?.finding?.reasonCode) add(t.yield.finding.reasonCode, d.name);
      if (d.kind === 'simulator') for (const sc of d.scenarios ?? []) for (const st of sc.steps ?? []) if (st.reasonCode) add(st.reasonCode, d.name);
    }
    const r = this.at(date);
    const rows = [...used].sort(([a], [b]) => a.localeCompare(b)).map(([code, by]) => {
      const set = Object.keys(r.sets).find((s) => r.sets[s][code] !== undefined && s.includes('Reason'));
      return { code, found: !!set, set, name: set ? r.sets[set][code] : undefined, usedBy: [...by].sort() };
    });
    return { version: r.version, provenance: r.provenance, rows, missing: rows.filter((x) => !x.found).map((x) => x.code) };
  }
}
