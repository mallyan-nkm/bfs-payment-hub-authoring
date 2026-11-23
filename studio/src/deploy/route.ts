import { routeOf } from '../interp/interpreter.js';
import { runTable } from '../rules/service.js';

// The front door's route of a start message (R-05-08, R-05-42, R-09-79): the dispatch table's route on the header, the platform
// workflow p.table.workflow-select.v1 chooses for it, and the leg (and refuse leg) in their slots. The deploy runner's front door
// (deploy.ts) and the runtime's (runtime/src/TransactHub.FileApi/FrontDoor.cs) work it out alike; one fixture holds both to it
// (runtime/tests/TransactHub.FileApi.Tests/front-door-routes.json, read by studio/test/front-door-route.test.ts).

export interface StartRoute {
  message: string;
  scheme: string;
  intakeMap: string;
  route: Record<string, string>;
  workflow: string;
  legs: Record<string, string>;
}

/** The scheme and dispatch entry of a message among the documents' schemes. */
export function entryOf(schemes: any[], message: string): { scheme: any; entry: any } | undefined {
  for (const s of schemes) {
    const e = (s.messages ?? []).find((m: any) => m.id === message);
    if (e) return { scheme: s, entry: e };
  }
  return undefined;
}

/** The workflow p.table.workflow-select.v1 chooses for a header, on the evaluation date. */
export function selectWorkflow(resolve: (name: string) => any, header: Record<string, unknown>, evaluationDate: string): string | undefined {
  const sel = resolve('p.table.workflow-select.v1');
  if (!sel) throw new Error('The package has no p.table.workflow-select.v1 for the front door');
  return runTable(sel, { Header: [header] }, { resolve, evaluationDate }).decisions.find((d: any) => d.name === 'workflow')?.value;
}

/** The route of a start message: what the runtime's front door computes for the same documents. */
export function startRoute(schemes: any[], resolve: (name: string) => any, message: string, evaluationDate: string): StartRoute {
  const found = entryOf(schemes, message);
  if (!found) throw new Error(`No scheme names the message ${message}`);
  const start = found.entry.disposition?.start;
  if (!start?.intakeMap) throw new Error(`${message} does not start a payment`);
  const route = routeOf(start);
  const workflow = selectWorkflow(resolve, route, evaluationDate);
  if (!workflow) throw new Error(`p.table.workflow-select.v1 chooses no workflow for ${message}`);
  const leg = resolve(start.leg);
  if (!leg) throw new Error(`The leg ${start.leg} that ${message} starts is not in the package`);
  const legs: Record<string, string> = { [leg.implements?.slot ?? 'scheme-leg']: start.leg, ...(start.refuseLeg ? { 'scheme-refuse': start.refuseLeg } : {}) };
  return { message, scheme: found.scheme.scope, intakeMap: start.intakeMap, route, workflow, legs };
}
