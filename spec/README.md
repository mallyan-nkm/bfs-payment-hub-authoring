# TransactHub Designer Studio: specification set

Status: draft 0.2, 17 November 2026. For review at gate G1. What changed since draft 0.1 is in `delta/DELTA-0.2.md`.

The Designer Studio is where a payments workflow designer sees the whole ISO 20022 data capability, the rules, the activities, the schemes and the simulators, and builds, tests and exports a payment workflow. This repository is its specification, written to be read by people and worked by Claude Code.

## What is in the set

| Layer | Where | What it is | Who reads it |
| --- | --- | --- | --- |
| Design spec | `design/` | Twelve parts and four annexes. Every requirement has an id and names the check that enforces it | Architects, reviewers, Claude Code |
| Schemas | `schemas/` | JSON Schema 2020-12 for every artefact kind. The machine-readable half of the design | Tools, Claude Code |
| Guard-rails | `CLAUDE.md` | The rules for any work in the repository, and the common Definition of Done | Claude Code, every session |
| Build specs | `build/` | One file per component. Each story is a prompt: user story, Definition of Ready, Done and Success | Claude Code, the delivery lead |
| Factory pack | `factory/` | Default content for `p`, `uae.p` and `uae.wps`. The worked example and the fixture set | Designers, Claude Code |
| Examples | `examples/` | An example bank tenant, engine bindings, a supersession | The same |
| Expanded forms | `expanded/` | What an engine runs for each workflow that calls a composite. Generated | Platform engineers, Claude Code |
| Conformance kit | `conformance/` | The scenarios every engine must pass | Platform engineers |
| Deltas | `delta/` | What changed between drafts, written for a build already under way | Claude Code, the delivery lead |
| Tools | `tools/` | Generator, validator, mutation tests, document renderer | Everyone |

<!-- COUNTS -->
| What | Count |
| --- | --- |
| Design parts and annexes | 12 and 3 |
| Numbered requirements | 302 |
| Schemas | 16 |
| Factory artefacts in `p`, `uae.p`, `uae.wps` | 159 |
| Example bank artefacts | 17 |
| Composite activities, each with a subflow | 7 |
| Test cases in the packs | 24 |
| Conformance scenarios | 50 |
| Checks in `tools/validate.py` | 32 (L00 to L41) |
| Mutation cases that prove them | 56 |
| Build components, stories, story points | 25, 125, 502 |
<!-- /COUNTS -->

## Reading order

1. `design/01-concepts-scopes-names-versions.md`: scopes, names, versions, pins, hot update.
2. `design/05-workflow-dsl.md`: the DSL and the check catalogue.
3. `design/12-factory-pack.md`, then one annex: A for WPS, D for the platform.
4. One workflow, the subflow behind its submit step, its expanded form and its tests: `factory/uae.wps/workflows/uae.wps.wf.salary-file-out.v1.yaml`, `uae.wps.sub.submit-salary-file.v1.yaml` beside it, `expanded/`, and `factory/uae.wps/tests/`.
5. `build/00-backlog.md`: the components, the phases and the build order.

The other parts are reference: canonical context (2), activities (3), rules (4), tenants and hooks (6), simulators (7), scripts and AI authoring (8), export (9), studio functions (10), packs and governance (11), the conformance kit (Annex C).

## Using it with Claude Code

1. Put this repository at the root of the studio's code repository, or beside it, with `CLAUDE.md` at the root.
2. Choose the studio's own stack in a short decision record. The spec fixes only the script languages (Python, TypeScript), the rule semantics (after NRules) and the three engines.
3. Open `build/00-backlog.md` and take the first story in build order.
4. Give Claude Code the story block as the prompt, in plan mode. Review the plan. Then let it build on the story's branch.
5. Accept the story when its Definition of Done holds and its Definition of Success is shown with evidence.

## Checking the set

```
pip install -r tools/requirements.txt
python3 tools/build_factory.py
python3 tools/validate.py
python3 tools/expand.py --check
python3 tools/mutation_tests.py
python3 tools/build_docs.py
```

As delivered: the validator reports no error and four warnings (the planned messages of the scheme, and three platform blocks that no leg calls, Issue I-38 in `docs/ISSUES.md`), the expanded forms are up to date, every mutation is caught, and the backlog renders with no error.

## What is confirmed and what is not

| Content | Status | Basis |
| --- | --- | --- |
| Design parts 1 to 12 | Draft for review | The north-star architecture and the decisions taken on 17 November 2026 |
| `p`, `uae.p` | Confirmed against the north star | North-star architecture; public ISO 20022 practice |
| `uae.wps` | Provisional | The 2018 CBUAE responsibility guide and public bank guides. Not the current rulebook |
| ISO reason codes in rules | Not verified | Written from memory; check against the current external code set |
| Test traces and samples | Not run | Tests name samples and expected traces. Nothing executes them until C09 and C11 exist |
| Story sizes | Estimates | Made from the spec; revise after each component's first story |
| Engine mappings in Part 9 | Designed, not proven | The conformance kit proves them, engine by engine |

Part 12, section 12.6 lists every known gap in the factory pack. Each annex ends with its open points.

## Open decisions

Nine decisions are open. None blocks the start of phase 2; each is isolated behind an interface or settled in the phase that needs it. `build/00-backlog.md` lists them with the components they touch.

## Conventions

- British English, short sentences, no em-dashes.
- RFC 2119 words in capitals where a requirement is meant.
- A requirement is a row: id, requirement, check. Mechanisms over assertions.
- A problem is an Issue and a Recommendation.
- A story is a user story with a Definition of Ready, Done and Success, sized 1, 3, 5 or 8 points, on its own branch.
