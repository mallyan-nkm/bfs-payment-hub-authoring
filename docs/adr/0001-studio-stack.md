# ADR 0001. The studio's own stack

Status: accepted for the walking skeleton, 17 November 2026. To confirm at G1.

## Context

The spec fixes three things only: scripts-behind in TypeScript and Python (Part 8), rule semantics after NRules (Part 4), and Temporal, DTFx and Flowable as targets (Part 9). The README asks for a short decision record on the rest before C01 starts.

The studio is a design-time tool. It must run workflows on its own interpreter with a virtual clock (R-10-04), run TypeScript scripts-behind in a sandbox (C07), offer every function without writing code (R-10-02), and never connect to a running payment (R-10-01).

## Decision

| Concern | Choice | Why |
| --- | --- | --- |
| Language | TypeScript on Node.js 22, one package in `studio/` | One language for the server, the interpreter, the rule service, the sandbox and the web app. TypeScript is already a script language, so the sandbox needs no second runtime. |
| Web app | React 18 and Vite, plain CSS | A single-page app over the HTTP API. No UI framework, so the look is ours to change. |
| HTTP | `node:http` with a small router (`src/server/api.ts`) | Few routes, no framework to learn or patch. |
| Storage | Files: the spec repository is read as published content; the studio's drafts, releases and audit are one JSON file per workspace (`studio/data/`) | Matches D8 (artefacts as files per tenant) without deciding it. The store hides the choice behind `Store`. |
| Schemas | Ajv for JSON Schema 2020-12, reading `spec/schemas` directly | The schemas stay the source of truth. |
| YAML | `yaml` with the integer tag narrowed to match PyYAML | The spec's own tools read YAML 1.1 with PyYAML; see Issue I-05. |
| Rule service | A TypeScript evaluator with NRules semantics behind `runRuleSet` and `runTable` | D13 (NRules on .NET) stays open. The interface is one session per call, facts in, findings and decisions out, so an NRules service can replace it. |
| Script sandbox | A worker process per tenant with nothing of the studio in it (R-08-20, Issue I-119): Node's permission model with no grant, an empty environment, no network (a namespace on Linux), memory and time limits, JSON over stdio; inside it a `node:vm` context with only the injected interface, a deterministic `Date`, no `Math.random`, a guard on reads and a check on writes | The process is the boundary a shared installation needs; the context keeps honest scripts deterministic. |
| Python scripts | One isolated `python3 -I -S` process per call, a restricted set of builtins and pure modules, JSON lines to the studio for port and rule calls | The same interface and the same simulators, rule service and virtual clock as TypeScript; a parity suite checks both give the same result. It runs in a worker process with an empty environment, no network (a namespace on Linux), Landlock and an audit hook, and memory and time limits (R-08-20, Issue I-119). |
| Tests | Vitest | Fast, TypeScript native. |
| Export | Generated files in a reproducible zip | R-09-15. No engine SDK is a dependency of the studio. |
| AI assistant | `@anthropic-ai/sdk`, model `claude-opus-5-5`, structured output, server-side refusal fallbacks | Drafts go through the same gates as a designer's; publish stays with maker-checker. Needs `ANTHROPIC_API_KEY` or an `ant` profile on the server. |
| Engine harnesses | One per target under `studio/engines/`, each with its own toolchain | Keeps engine SDKs out of the studio; the kit adapter interface (`KitAdapter`) is the contract. |

## Consequences

- The interpreter in `src/interp/` is the reference for the conformance kit. An engine adapter replaces `runScenario` in `src/conformance/kit.ts` and keeps the trace comparison.
- Engines run beside the studio, not inside it. `studio/engines/dtfx/` (C#, DurableTask.Core on a time-skipping in-process orchestration service) and `studio/engines/flowable/` (Java, embedded Flowable 7.1.0 with H2) run the conformance kit through the party service (`src/conformance/party.ts`), which supplies stubs, scripts and simulators on the engine's clock. Temporal is not built: see the README.
- A bank's scripts run in a worker process of their own, one tenant per worker (R-08-20). A platform without namespaces or Landlock keeps fewer layers (Issue I-147).

## Open decisions this record does not close

D7, D8, D9, D10, D13, D14, D15, D16, D17, as listed in `spec/build/00-backlog.md`. Where the walking skeleton needed an answer it took the one the spec states as the default and isolated it:

| Decision | What the skeleton does | Where to change it |
| --- | --- | --- |
| D13 NRules as the evaluator | TypeScript evaluator, same semantics | `src/rules/service.ts` |
| D14 Where scripts run | In a worker process per tenant (R-08-20) | `src/interp/isolation.ts`, `src/interp/script-worker.cjs` |
| D15 Supersession policy | Explicit only; the run applies a supersession it is given | `Run.resolveFor` in `src/interp/interpreter.ts` |
| D16 Default lookup date | Step time, as R-04-10 says; `lookupDate: business` reads the business date | `Run.lookupParam` |
| D17 Where handlers run | As a non-durable run with no instance record | `startInstance(..., 'handler')` |
