# Extraction prompt: a scheme's source code into a studio import bundle

Point an AI coding agent (for example Claude Code) at the source code of a scheme that is written as workflows, such as WPS, with this prompt. It writes an **extraction bundle**: the scheme's configuration, code and content, in the TransactHub Designer Studio's own formats. The studio's import (I-33) then checks the bundle, runs its tests, and saves it as drafts for maker-checker.

## How to use it

1. Check out two folders side by side: the source system (`SOURCE`) and this repository (`STUDIO`, with `spec/` and `studio/`). The agent needs read access to both, and `npm install` run once in `STUDIO/studio`.
2. Start the agent in a working folder, and give it everything below the line **The prompt**, with the five values at its top filled in.
3. The agent works through the phases, and checks its own bundle with the studio's importer until the import reports no errors.
4. In the studio, open **Import**, choose the bundle zip, read the preview, and import it. Then an NPSS or WPS expert reviews the findings and the unmapped items, and a checker publishes the change sets.

To see what a finished bundle looks like, download the training bundle from the Import page, or run `npm run studio -- import-sample extraction-sample.zip` in `studio/`.

---

## The prompt

```
SOURCE      = <path to the source system's repository>
REVISION    = <its commit or tag>
STUDIO      = <path to the TransactHub Designer Studio repository>
SCOPE       = <the scheme scope the bundle builds, for example uae.wps>
BANK_TENANT = <the bank tenant that receives bank values and hooks, or none>
OUT         = <the folder to write the bundle into>
```

You are extracting a payment scheme from its source code into the TransactHub Designer Studio. The source is written as workflows: orchestrations, activities, services, validators, message formats, configuration and tests. The studio describes a scheme as content: activity contracts, rules and rule sets, decision tables, maps, ports and simulators, workflows (legs, segments, subflows, handlers), a scheme manifest with its dispatch table, extensions, parameters, a bank's configuration and hooks, tests and samples, and a small amount of code as scripts-behind. Your job is to turn all of the source into that content, and to account for every file and every unit of it. A reviewer will check that nothing was lost, so comprehensiveness matters more than speed.

### Read first

1. `STUDIO/spec/CLAUDE.md`: the guard-rails. They bind you.
2. `STUDIO/spec/design/`: parts 01 (names, scopes, versions), 02 (the canonical context), 03 (activity contracts), 04 (rules), 05 (the workflow language), 06 (tenants, hooks, configuration), 07 (simulators), 08 (scripts-behind).
3. `STUDIO/spec/schemas/`: every document you write must validate against its schema.
4. `STUDIO/spec/factory/`: the platform (`p`), the UAE cluster platform (`uae.p`) and the factory schemes (`uae.wps`, `uae.aani`). Use them as the pattern for every document, and reuse their blocks wherever the source does the same thing.
5. `STUDIO/docs/import/EXTRACTION-PROMPT.md`, "The bundle": the format you write.

### Guard-rails (not negotiable)

- Names are `<scope>.<type>.<name>.v<major>`. Scopes are `p`, `<cluster>.p`, `SCOPE` and `b`. Never put a bank, tenant or vendor name in a shared name, title or description.
- A workflow holds flow only. Validation, decisions and lookups run in activities, through rules and tables.
- Every outcome other than the primary one is handled. Nothing else is.
- A value a bank may change is a parameter with a default and bounds, never a constant. The bank's actual values go in its configuration set.
- Bank-specific behaviour is a rule value or a hook at a hook point. A bank-specific activity in a shared workflow is an anti-pattern: report it as a finding and map it to a bank rule or hook.
- A leg fills a slot of the platform workflow. Never clone a platform workflow for a scheme or a bank.
- An `await` and a `return` appear only in a subflow. A send-and-wait is a composite activity whose subflow sends, waits and reads.
- The context is the canonical ISO 20022 model. A scheme field that has no canonical home is a registered extension field. There is no untyped field.
- A script-behind uses only `ctx`, `ports`, `rules`, `params`, `clock`, `ids`, `log`. No network, file system, system clock, randomness or environment variables.
- **No personal data anywhere**: not in samples, tests, examples, findings or comments. Samples are synthetic, made from the formats, never copied from production files or logs. Replace any real name, account, ID, phone or e-mail you meet with a made-up value.
- Never invent a scheme fact. Where the source and the spec disagree, or neither says, write a finding (a question or a difference). Do not guess.
- Every document cites where it came from: an inventory item names the file, the lines and the symbol.
- Provenance: `origin: authored`, `status: provisional`, and `clauses` naming the source and the spec clause, until an expert validates it.

### Method

Work in these phases, in order. Keep notes in `OUT/notes/` as you go.

**Phase 0. Every file.** List every file under `SOURCE` that you read, in `extraction.yaml` under `source.files`. Files you deliberately do not extract (build scripts, generated code, vendored libraries, binaries, unrelated modules) go under `source.excluded`, each with a reason. Every file must be in one list or the other: the importer refuses a bundle with a file in neither.

**Phase 1. Every unit.** Go through every file and list every unit of behaviour or configuration in `inventory.yaml`. Look for all of these, and record each one you find as its own item:

| Look for | `source.kind` |
| --- | --- |
| Workflows, orchestrations, sagas, state machines, durable functions | `workflow` |
| Steps, activities, handlers, commands, jobs a workflow calls | `activity` |
| Service clients, repositories, gateways, adapters to other systems | `service` |
| Validations: if-checks, validators, regular expressions, limits, required fields, cross-field and total checks | `rule` |
| Lookup tables, enums, code lists, routing tables, decision logic over several inputs | `table` |
| Parsers, serialisers, field mappings, file and message layouts | `map` |
| Message types, message catalogue entries, routing of inbound messages | `message` |
| Endpoints, queues, topics, files and folders exchanged with another party | `port` |
| Configuration keys: settings files, environment variables, feature flags, configuration tables in a database | `config` |
| Error types, error codes, reason codes, exception handling | `error` |
| Retry policies, back-off, idempotency, duplicate detection | `retry` |
| Timers, timeouts, cut-off times, business calendars, holidays | `timer` |
| Status values, persisted state, status transitions | `state` |
| Scheduled jobs, batch windows | `schedule` |
| Unit, integration and end-to-end tests, fixtures, test data | `test` |
| Sample files and messages | `sample` |
| Code that must stay code (a parser too irregular for a map, an algorithm) | `script` |
| Screens and forms that change behaviour or configuration | `ui` |
| Connection strings, credentials, hosting, monitoring, deployment | `infrastructure` |
| Anything else | `other` |

For each item: `id` (SRC-0001, SRC-0002 ...), `source` (`path`, `lines`, `symbol`, `kind`), a one-line `summary`, a `target` (below), `confidence` (high, medium, low), and a `note` where it helps the reviewer.

**Phase 2. Where each unit goes.** Decide its `target.kind`:

- `artefact`: it becomes (or is already) a studio document; `target.name` is that document's name (`scheme:<scope>` for the scheme manifest).
- `parameter`: a value a bank may change; `target.name` is `<rule or table name>#<parameter>`.
- `configuration`: a bank's actual value; it goes in that bank's configuration set.
- `bank-rule` or `bank-hook`: bank-specific behaviour, as a rule or a hook in scope `b`.
- `sample`, `test`: the sample or test that carries it.
- `infrastructure`: hosting, credentials, endpoints and monitoring. Not imported; it feeds the infrastructure roadmap. Say what it needs.
- `retire`: dead code, or behaviour the platform already gives. Say why.
- `unmapped`: you could not map it. Say why. Unmapped items are allowed, but each one is listed for the expert.

Before you write a new block, search `STUDIO/spec/factory/` for one that does the same thing. For every item that lands in `p` or `uae.p`, set `reuse`: `as-is` (an existing block, unchanged), `with-change` (an existing block that needs a new release), or `new`.

**Phase 3. The documents.** Write each document in `artefacts/`, one per file, named `<name>.yaml`, in a subfolder by kind. Map the source like this:

| In the source | In the studio |
| --- | --- |
| The scheme's end-to-end processing for one kind of payment | A `leg` that fills the `scheme-leg` slot of the platform workflow, with its `implements`, `entry`, `exit` |
| Shared processing used by several workflows | A `segment` |
| A send that waits for a reply or acknowledgement | A composite `exchange` activity with `binding: subflow`, and its `subflow` (send, await, read, return) |
| Processing of an inbound message with no state | A `handler` |
| Each step that calls a service or does a job | An activity contract: `type`, `level`, `reads`, `writes`, `outcomes` (one primary), `errors`, `effect`, `policy`, `idempotencyKey` where it writes outside and may be retried, `ports` |
| Validations | `rule` documents (when, then, a reason code), grouped in a `ruleset`, run by a `validate` activity bound to `builtin: rule-service`. Every rule has examples: one that passes, one that fails, and one at each boundary |
| Decision logic | A decision `table` with a hit policy and examples, or a `decision` rule |
| Parsers and formatters | `map` activities (`direction: in` or `out`), with their field list; scheme fields go in an `extension` |
| Endpoints and queues | A `port` per party, with a `simulator` that has the standard scenarios, played from the behaviour you observed |
| The message catalogue and inbound routing | The scheme manifest (`kind: scheme`) and its dispatch table: `start` (kind, direction, segment, intake map, leg), `deliver` (correlation), `handler`, `sentBy` |
| Constants a bank changes (limits, cut-offs, codes) | `parameters` with a default and bounds on the rule or table that uses them |
| A bank's actual values | `configuration/<bank tenant>.yaml`: a configuration set with `values` and hook `bindings`, each with `validFrom` |
| Bank-specific branches | A bank rule, or a `hook` at a declared hook point, in scope `b` |
| Retry and timeouts | The activity's `policy` (attempt timeout, total timeout, retry with interval and back-off) |
| Cut-off times and calendars | Parameters and the calendar port, never hard-coded dates |
| Code that must stay code | An activity with `binding: script` and its script-behind |

**Phase 4. Scripts-behind.** For each activity bound to a script, translate the source code into `scripts/<activity name>.ts` (or `.py`). Export `run({ ctx, ports, rules, params, clock, ids, log })` and return the outcome. Read and write only the paths the contract declares. Call other parties only through `ports`, rules only through `rules`, values only through `params`, time only through `clock`. Keep it short: logic that can be a rule, a table or a map should be one.

**Phase 5. Tests and samples.** Write `tests/<id>.yaml` test documents: at least one per outcome of every activity you wrote; for each leg, accept, reject, no reply (then a status inquiry, never a resend), late reply and duplicate; and one per scenario in the source's own tests. Each test names its target, level, evaluation date, bench bindings (port, simulator, scenario), what it is given (a context, or an inbound sample and its start message) and what it expects (outcome, trace, reason code). Write `samples/<name>` synthetic sample files from the formats, valid and invalid, named in lower case with hyphens. No real data, ever.

**Phase 6. Findings.** Write `findings.yaml`:
- `difference`: where the code and the scheme spec disagree, with both sides;
- `question`: what neither says, for the expert to answer;
- `error-scenario`: retries, timeouts, partial failure, state left behind, and how the studio content handles each;
- `risk`: anything that may not hold, and why;
- `decision`: a choice you made, and why.

Each finding names its `source` and, where it applies, the `artefact`.

**Phase 7. Check it with the studio.** From `STUDIO/studio`:

```
npm run studio -- import OUT --bank-tenant BANK_TENANT
```

It prints every error and warning, the coverage of the source, and runs every test of the bundle against the bundle itself. Fix what it reports and run it again, until it reports no errors and every test passes. Then zip `OUT`. Do not import it with `--apply`: a person does that.

### Definition of done

- [ ] Every file of the source is in `source.files`, and either inventoried or excluded with a reason.
- [ ] Every unit of every file is an inventory item, with its path, lines and symbol.
- [ ] Every item is mapped: to a document, a parameter, configuration, a bank rule or hook, a sample, a test, infrastructure, retired with a reason, or unmapped with a reason.
- [ ] Every document validates, passes the studio's static checks, and is explained by at least one item.
- [ ] Every block in `p` or `uae.p` is marked reused as is, reused with change, or new.
- [ ] There is no bank-specific activity in a shared workflow, and no bank or vendor name in shared content.
- [ ] Every value a bank may change is a parameter with bounds; the bank's values are in its configuration set.
- [ ] Every activity has a test per outcome; every leg has its accept, reject, no-reply, late and duplicate tests; every test passes in the import preview.
- [ ] Every script uses only the injected interface.
- [ ] No personal data anywhere.
- [ ] Differences between code and spec, open questions and error scenarios are in `findings.yaml`.
- [ ] `npm run studio -- import OUT` reports no errors.

### What to hand back

A short summary: the counts the importer printed, the unmapped items with their reasons, the differences between code and spec, the questions for the expert, and the share of the leg that is content against code.

---

## The bundle

```
OUT/
  extraction.yaml          the manifest
  inventory.yaml           every unit of the source, and where it went
  findings.yaml            differences, questions, error scenarios, risks, decisions
  artefacts/**/*.yaml      studio documents, one per file (activities, rules, rulesets, tables,
                           maps, ports, simulators, extensions, workflows, hooks, the scheme)
  scripts/<activity>.ts    scripts-behind (or .py)
  samples/<name>           synthetic sample files
  tests/<id>.yaml          test documents
  configuration/<bank>.yaml  a bank's configuration set (values and hook bindings), optional
  README.md, notes/        for people; not imported
```

### `extraction.yaml`

```yaml
kind: extraction
format: 1
scope: uae.wps
title: WPS, extracted from the salary file service
source:
  system: WPS salary file processing
  repository: <repository name or URL>
  revision: <commit or tag>
  engine: <the workflow engine the source runs on>
  languages: [csharp, sql, json]
  files:                      # every file read
    - src/Workflows/SalaryFileWorkflow.cs
    - src/Validators/SifValidator.cs
    - config/appsettings.json
  excluded:                   # files deliberately not extracted, with why
    - { path: src/Generated/Proxies.cs, why: Generated client code }
extractedBy: { tool: Claude Code, model: <model>, date: 2026-11-19, promptRef: docs/import/EXTRACTION-PROMPT.md }
validatedBy: { expert: <name>, date: <date> }    # once an expert has reviewed it
dataPolicy: synthetic
```

### `inventory.yaml`

```yaml
items:
  - id: SRC-0001
    source: { path: src/Validators/SifValidator.cs, lines: 40-58, symbol: ValidateSalaryMonth, kind: rule }
    summary: The salary month is MMYYYY and a real month
    target: { kind: artefact, name: uae.wps.rule.salary-month-valid.v1 }
    confidence: high
  - id: SRC-0002
    source: { path: config/appsettings.json, lines: 12-12, symbol: Wps:MaxRecords, kind: config }
    summary: The most records a file may hold
    target: { kind: parameter, name: uae.wps.rule.record-count-limit.v1#maxRecords }
    confidence: medium
    note: Default 10000 in the code; the guide does not say. Asked in findings.
  - id: SRC-0003
    source: { path: src/Clients/CoreBankingClient.cs, symbol: Connect, kind: infrastructure }
    summary: Connection to the core banking system
    target: { kind: infrastructure }
    note: Needs a native binding for p.port.core-banking.v1 at the bank.
```

### `findings.yaml`

```yaml
findings:
  - kind: difference
    text: The code accepts a salary month up to 13 months back; the guide says the current or previous month.
    source: src/Validators/SifValidator.cs:40-58
    artefact: uae.wps.rule.salary-month-valid.v1
  - kind: question
    text: Is a file with zero records rejected, or acknowledged and ignored?
    source: src/Workflows/SalaryFileWorkflow.cs:120-131
```

### What the importer checks

| Check | Code |
| --- | --- |
| The manifest: kind, format, scope, source, every file read, synthetic data | I10 to I13 |
| Bank content has a bank tenant to go to | I14 |
| Each document: its schema and every static check of the studio, with the rest of the bundle in place | L00 to L33 |
| Each document is in the bundle's scope, its cluster platform, `p` or `b` | I22 |
| No personal data in any document, script, sample or finding | I30 |
| Each document is explained by an item of the inventory | I40 |
| Each script belongs to an activity bound to a script, defines `run`, and uses only the injected interface | I50, I51, L28 |
| An activity bound to a script without one runs on the reference behaviour | I52 (warning) |
| Each inventory item is complete, and maps to something that exists | I60, I61 |
| A retired or unmapped item says why | I62 |
| Every file of the source is inventoried or excluded | I64 |
| A published release is never changed: a changed block gets a new release, proposed by its interface change | R-01-10 |
| Every test of the bundle passes against the bundle itself, before anything is saved | I80 (warning) |

Importing saves everything as drafts: documents (unchanged ones are reused, not saved again), scripts, samples, tests and the bank's configuration. Nothing is published: a checker who is not the maker approves each change set in Governance.
