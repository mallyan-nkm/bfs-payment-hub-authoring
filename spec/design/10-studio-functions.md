# Part 10. Studio functions

Status: draft 0.2, 17 November 2026. Normative words follow RFC 2119.

## 10.1 Purpose

This part lists what the payments workflow designer can do in the studio. It is functional: it says what, not how the screen looks. The studio is a design-time tool. It never touches a running payment.

## 10.2 The loop

Define, script, test, publish, compose, export. Every artefact kind follows it.

## 10.3 Functions

| Id | Area | The designer can | Acceptance |
| --- | --- | --- | --- |
| F-01 | Catalogue | Browse and search every artefact she may see, by scope, kind, type and text | Finds `uae.p.validate.iban.v1` by any of name, title or path read |
| F-02 | Catalogue | See for any artefact its contract, releases, tests, and who uses it | Usage list matches lint L03's reference graph |
| F-03 | ISO data | Search any canonical element, type or code and see which blocks read and write it | Lists every reader and writer of `payment.instructedAmount` |
| F-04 | ISO data | See a scheme's message catalogue with dispositions | Shows the dispatch table of `factory/uae.wps/scheme.yaml` |
| F-05 | ISO data | Generate valid and invalid sample messages per scheme | A valid sample passes the scheme rule sets; an invalid one fails as marked |
| F-06 | Maps | Define a map both ways and run round-trip tests | `WPS-MAP-001` and `WPS-MAP-002` pass |
| F-07 | Rules | Author a rule as when and then, with parameters, bounds and examples | The rule validates and its examples run green |
| F-08 | Rules | Author a decision table and see gaps and overlaps | A `unique` table with an overlap is refused |
| F-09 | Activities | Define a contract in a form: reads, writes, outcomes, errors, effect, ports, policy | The saved document validates against `activity.schema.json` |
| F-10 | Activities | Run an activity alone, or against simulators, and see the context diff | `P-RES-001` passes with three attempts shown |
| F-11 | Simulators | Define a port's simulator and its scenarios | The standard scenario set is present before publish |
| F-12 | Workflows | Create a platform workflow, leg, segment or handler: blank, from a template, or by cloning a release | A new workflow saves as a draft and validates |
| F-13 | Workflows | Compose steps from the catalogue, with slots, hook points, policies and `finally` | Every construct of Part 5 can be placed and edited |
| F-14 | Workflows | See every static check on every edit, with the step it concerns | Removing a `handle` entry shows L05 at that step |
| F-15 | Workflows | See the effective policy of a step and where each value came from | Shows type default, block default and step override |
| F-16 | Test bench | Bind simulators to ports, set the evaluation date and the tenant, and run | A run with `no-reply` ends at the timeout on the virtual clock |
| F-17 | Test bench | Step through a run; see the timeline of attempts, waits and timers; see the context diff per step | Timeline of `AANI-OUT-003` shows three attempts and the backoff |
| F-18 | Test bench | Send a fixed signal during a run | `cancel` runs compensation in reverse, then `finally` |
| F-19 | Test bench | Assert on outcome, status, messages, postings and trace | A changed trace turns the test red |
| F-20 | AI | Ask AI to draft a contract, rule, script, simulator or map from scheme text and samples | The draft arrives with tests and passes every gate, or shows the red gate |
| F-21 | AI | See provenance for any AI-authored artefact | Model, prompt reference, inputs and clauses are shown |
| F-22 | Versions | Publish a release through maker-checker; see the semver the interface diff implies | A changed outcome list proposes a major |
| F-23 | Versions | See, for a new release of a block, every workflow on that line, with its rerun result and the proposed new workflow release | Lists workflows and offers the release |
| F-24 | Versions | See which releases are pinned by published workflows and which may retire | A pinned release cannot be removed |
| F-25 | Versions | Author and test a supersession | The pinned test pack reruns with the replacement swapped in |
| F-26 | Tenant | As a bank: see shared content read-only | An edit of a shared artefact is refused |
| F-27 | Tenant | As a bank: author a hook for a declared hook point, with its blocks, rules and simulators | A hook that writes outside its contract is refused (L13) |
| F-28 | Tenant | As a bank: set parameter values inside bounds and bind hooks, with effective dates | A value outside bounds is refused (L14) |
| F-29 | Tenant | As a bank: see the compatibility run of its hooks against a new shared release before accepting it | The report lists each hook test and its result |
| F-30 | Export | Export a pack: shared, bank or test | The export passes `tools/validate.py` and carries pins |
| F-31 | Export | Choose a target engine and get the package and binding for it | The conformance kit passes on that target |
| F-32 | Audit | See who changed what, when, and who approved it | Every publish has a maker and a different checker |
| F-33 | Workflows | Place a composite as one block, handle its business outcomes, and set how long it waits for its reply | The WPS leg shows `submit` as one step with `replyTimeout` in its policy panel |
| F-34 | Workflows | Open a composite read-only to see the steps it expands to, and view a whole workflow in its expanded form | The expanded view equals `expanded/uae.wps.wf.salary-file-out.v1.yaml` |
| F-35 | Test bench | See the steps inside a composite nested under it in the timeline | `WPS-OUT-001` shows send, await and read under `submit` |
| F-36 | Activities | As a scheme owner: author a composite and its subflow, and test it alone | `WPS-EXC-001` and `WPS-EXC-002` pass |

## 10.4 Requirements

| Id | Requirement | Check |
| --- | --- | --- |
| R-10-01 | The studio MUST NOT read or change any running payment. | Architecture; no production connection exists |
| R-10-02 | Every function above MUST be available without writing code. A script is written by AI and reviewed by behaviour. | Usability test with a designer |
| R-10-03 | Every static check MUST run on every edit and name the step it concerns. | Studio test (C14) |
| R-10-04 | The studio MUST run workflows on its own interpreter with a virtual clock. | Conformance kit on the studio interpreter |
| R-10-05 | The studio MUST show a tenant only what Part 6 allows. | Isolation tests (C17) |
| R-10-06 | The studio MUST be usable for functional testing only. It MUST NOT offer load, back-pressure or soak tests. | Scope review |

## 10.5 Out of scope

Queue timeouts, heartbeats, rate limits, back-pressure and worker saturation. These are engine settings in the binding and are tested in pre-prod performance runs.
