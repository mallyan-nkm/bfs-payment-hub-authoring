# Designer Studio re-ideation: plan summary

As of 17 November 2026. The full plan is the Claude doc "TransactHub Designer Studio: re-ideation plan": https://claude.ai/artifact/ErZSWf73LdUotp3jWnGc8v. That doc is the source of truth for the plan; this note is a pointer and a record of the decisions taken while drafting it. The spec itself is a repository, `designer-studio-spec.zip`, delivered in chat on 17 November 2026 and described under "Spec set as built".

## What the studio is

The Authoring view (Scheme Studio) of the TransactHub north star, re-ideated for the payments workflow designer. She defines, scripts (with AI), tests and exports a payment workflow from composable blocks that all read and write one ISO 20022 canonical context. Functional testing only; no load or back-pressure. The studio is one multi-tenant application, with each bank as a tenant.

## Decisions given by Nitin

- Scopes: `p.*` (platform, not `platform.*`), `<cluster>.p.*` (for example `uae.p.*`), `<cluster>.<scheme>.*` (for example `uae.wps.*`, `uae.aani.*`), and `b.*` for the bank scope (accepted).
- There can be several named platform workflows, each versioned (for example corporate for RTGS and WPS, commercial for Aani). Creating a new workflow is a first-class act.
- Everything is versioned major.minor.patch: major for an interface change, minor for improved function, patch for a bug fix. The major is part of the name, and the exported DSL carries the version of each artefact.
- A pinned workflow executes with fixed pinned versions. How hot update of rules, hooks and activities affects a workflow had to be determined; the plan's answer is below.
- Intake maps convert any source model into the canonical ISO 20022 model; scheme legs map back out where needed.
- Script languages: Python and TypeScript, both.
- Rules: the rule definition DSL has NRules semantics.
- DSL lineage: evolve today's JSON definition and align construct names with the CNCF workflow DSL.
- Temporal, DTFx and Flowable (BPMN) are all delivery targets and the proof of portability.
- Hooks are first-class targets, like schemes, specifically for banks. A bank is a tenant in the studio.
- The app is multi-tenant per bank. Tenants share platform and scheme content; the only thing a bank authors is hooks. A bank cannot change a platform or scheme definition, but can change configuration such as rule values.
- There is default content for the platform, `uae.wps` and `uae.aani`: a factory pack. The whole spec was to be built, including it.

## Design positions in the plan and the spec (to confirm at G1)

- Seven artefact kinds: data, rules, activities, simulators, workflows, schemes, hooks.
- Name grammar: `<scope>.<type>.<name>.v<major>`, for example `uae.p.validate.iban.v1`.
- Each exported workflow document has an `artefacts` block pinning the exact release and hash of every artefact; a running instance executes exactly those.
- Hot update has three mechanisms. Lookup: rule values, lists and registries are effective-dated and read at each step, so they reach payments in flight. Release: rule logic, activities, maps, hooks and workflows are pinned, so a new release reaches only new payments. Supersession: an approved, tested replacement of one pinned release by a later one of the same major line, applied by Service operations with a second approver, forward only.
- This narrows the north star's "rules looked up" to rule values, lists and bindings; rule logic is pinned.
- Hook points are declared by the workflow owner and typed halt, transform or notify. A bank binds hooks through its configuration set; an instance takes the bindings in force when it starts.
- Configuration is values, not definitions: shared content exposes named parameters with bounds; a bank sets values inside the bounds.
- A tenant exports: shared packs unchanged, its bank pack, its configuration set.
- Rule DSL: when (match, exists, not, all, query, let, having) and then (yield a finding or decision). One session per rule-set call; validation sets do not chain; decision sets may chain and their yield graph must be acyclic; parameters are inserted as facts.
- Workflow kinds: platform workflow, scheme leg, segment, handler. A message gets a workflow only if it needs durable state. Status reports and ACKs are awaited by the running leg; inbound status inquiries, proxy lookups and similar are stateless handlers; recalls, returns, refunds and requests to pay are their own legs. Each scheme pack has a dispatch table covering every message.
- Eleven activity types: map, validate, enrich, screen, decide, reserve, exchange, post, notify, hold, reconcile.
- Timeouts and retries are declared policy, tested on a virtual clock against simulator scenarios, mapped per engine at export.
- Export: interpreter on Temporal and DTFx; generated BPMN on Flowable.

## Changes made while building the spec

- DSL keys: `handle` on a call or slot and `by` on a switch. `on`, `off`, `yes` and `no` are forbidden as keys because YAML reads them as booleans.
- Inward platform workflows have a second slot, `scheme-refuse`, so that a leg can answer the scheme when the platform refuses a payment.
- A fourth platform workflow, `p.wf.request.v1`, runs requests that are not payments, such as a WPS refund order.
- Platform workflows have a `finally` block and a `review` exit outcome. Legs may end `pending`.
- An activity that no workflow calls declares `invokedBy: front-door` or `invokedBy: schedule`.
- `uae.p` blocks are called by legs, never by `p` workflows; `uae.p` rules are also reused one by one in scheme rule sets.
- Sending and waiting for a reply: an activity never waits for a later reply. A synchronous reply stays inside one `exchange` activity; a later reply is a send step then an `await` step; polling is a `loop` around an inquiry; a person is a `hold`. The reply is delivered by the front door through the dispatch table (a signal on Temporal, an external event on DTFx, a message catch on Flowable). Early messages are kept, late ones are recorded as unmatched, duplicates are ignored. An `await` names one catalogue message; waiting for the first of several is not in the portable core.

## Spec set as built

A repository that Claude Code reads directly.

| Layer | Where | Size |
| --- | --- | --- |
| Design spec | `design/` | 12 parts, 4 annexes (A WPS, B Aani, C conformance kit, D platform), 208 numbered requirements |
| Schemas | `schemas/` | 14, JSON Schema 2020-12 |
| Guard-rails | `CLAUDE.md` | One file, with the common Definition of Done |
| Build specs | `build/` | 26 components, 126 stories as prompts (user story, DoR, DoD, DoS), 505 story points |
| Factory pack | `factory/` | 157 artefacts: `p` 54, `uae.p` 13, `uae.wps` 51, `uae.aani` 39 |
| Example bank tenant | `examples/bank/` | 15 artefacts: one hook of each type, a configuration set |
| Conformance kit | `conformance/` | 43 scenarios |
| Tools | `tools/` | Generator, validator (20 checks), 21 mutation cases, document renderer |

Backlog by phase: phase 2 walking skeleton 54 stories and 213 points; phase 3 catalogue, AI and hooks 51 and 203; phase 4 portability 11 and 51; phase 5 governance 10 and 38. Sizes are estimates. Phase 2 is heavy for a skeleton; the recommendation is to cut it at G1 to the WPS salary-file leg end to end.

As delivered: the validator reports no error and two warnings (planned messages), and every mutation case is caught.

## Next

Review the spec set at G1. Choose the studio's own stack in a short decision record, then start with story C01-S1. Open decisions: D7 to D10 and D13 to D17 (script as production runtime, where artefacts live, synchronous segment, scheme source documents, NRules as the evaluator, where scripts run, supersession policy, lookup date default, where handlers run).

## Not verified

- Aani message set, time limits and reason codes (the AEP participant specification is not public). `uae.aani` is provisional.
- The current CBUAE WPS rulebook. `uae.wps` was written from the 2018 guide and public bank guides and is provisional.
- ISO reason codes in rules were written from memory.
- Scripts-behind, sample messages and the kit's own workflows do not exist yet. Tests name samples and expected traces; nothing has run them.
- Today's studio, JSON definition, rule engine and hook framework, and NRules beyond its package and API reference pages.
