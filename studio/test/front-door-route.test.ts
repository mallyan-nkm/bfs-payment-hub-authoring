import * as fs from 'node:fs';
import * as path from 'node:path';
import { describe, expect, it } from 'vitest';
import { startRoute } from '../src/deploy/route.js';
import { freshEnv } from './helpers.js';

// The deploy runner's front door routes every start message as the runtime's front door does (R-09-79): both are held to the same
// fixture, which runtime/tests/TransactHub.FileApi.Tests/FrontDoorRouteTests.cs reads too.

const FIXTURE = path.resolve(__dirname, '..', '..', 'runtime', 'tests', 'TransactHub.FileApi.Tests', 'front-door-routes.json');

describe('the front door route of a start message (R-09-79)', () => {
  it('is the runtime front door\'s, for every start message of the content branch', () => {
    const env = freshEnv();
    const schemes = env.store.byKind('scheme').map((e) => e.doc).filter((s: any) => s.release === env.store.scheme(s.scope).release);
    const resolve = (n: string) => env.store.get(n)?.doc;
    const actual: Record<string, any> = {};
    for (const s of schemes.sort((a: any, b: any) => a.scope.localeCompare(b.scope)))
      for (const m of (s.messages ?? []).filter((x: any) => x.disposition?.start).sort((a: any, b: any) => (a.id < b.id ? -1 : 1))) {
        const r = startRoute([s], resolve, m.id, '2026-11-23');
        actual[r.message] = { scheme: r.scheme, intakeMap: r.intakeMap, route: r.route, workflow: r.workflow, legs: r.legs };
      }
    expect(actual).toEqual(JSON.parse(fs.readFileSync(FIXTURE, 'utf8')).routes);
  });

  it('refuses a message that starts nothing', () => {
    const env = freshEnv();
    const resolve = (n: string) => env.store.get(n)?.doc;
    expect(() => startRoute([env.store.scheme('uae.wps')], resolve, 'uae.wps.psi', '2026-11-23')).toThrow(/does not start a payment/);
    expect(() => startRoute([env.store.scheme('uae.wps')], resolve, 'uae.wps.none', '2026-11-23')).toThrow(/No scheme names/);
  });
});
