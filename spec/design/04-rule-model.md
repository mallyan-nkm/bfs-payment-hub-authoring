# Part 4. Rule model

Status: draft 0.1, 17 November 2026. Normative words follow RFC 2119.

## 4.1 Purpose

Rules are versioned data that return findings or decisions. They never change the payment and never call out. The rule definition DSL has NRules semantics: a `when` that matches facts and a `then` that acts on the match. This part fixes that DSL and narrows its action side.

## 4.2 Kinds

| Kind | Question | Form |
| --- | --- | --- |
| Structural | Is the message well-formed for this scheme? | Rule, usually an edge rule inside a map |
| Field | Is this value valid on its own? | Rule |
| Cross-field | Do the values agree? | Rule, often with a query |
| Reference | Does it exist in a registry? | Rule over reference-data facts |
| Policy | Is it allowed now? | Rule with a parameter |
| Decision | Which option applies? | Decision table |
| Stateful check | Seen before, or over a running total? | An activity, not a rule |

## 4.3 The DSL

| Element | NRules meaning | Restriction |
| --- | --- | --- |
| `when.match` | Match a fact of a type under conditions, and bind it | Fact types come from 4.4 |
| `when.exists`, `when.not`, `when.all` | Existential, negative and universal quantifiers | None |
| `when.query` | Aggregate matched facts: collect, group | None |
| `when.let`, `when.having` | Bind a calculated value; filter on bound values | Expressions are side-effect free |
| `when.or`, `when.and` | Group conditions | None |
| `then.yield.finding` | Produce a fact from the match | The action of a validation rule |
| `then.yield.decision` | Produce a fact from the match | The action of a decision rule |
| `then.yield.fact` | Produce a fact other rules may match | Decision sets only |
| `priority` | Among rules activated together, the higher runs first; default zero | Decision sets only |
| `repeatability` | Whether a rule fires again on the same facts | Fixed to non-repeatable |

### Facts

| Fact | Built from |
| --- | --- |
| `Header`, `Payment`, `Batch`, `Item`, `Status`, `Facts` | The declared context paths of the calling activity |
| An extension fact, for example `WpsBatch`, `WpsItem` | A registered extension, one fact type per level |
| `Param` | One per declared parameter, with `name` and `value`, resolved before the session starts |
| `Reference` | Reference data the calling activity looked up |
| `SourceFile` | Edge rules only: the name and size of the source file |

### Expressions

A small typed expression language: literals, paths on bound variables, comparison, `and`, `or`, `not`, arithmetic, `in`, and a fixed library of pure functions: `isBlank`, `len`, `startsWith`, `matches`, `count`, `sum`, `sumWhere`, `min`, `max`, `concat`, `ibanValid`, `round2`. `sumWhere(xs, x => condition, x => value)` sums the values of the items the condition holds for. `round2(x)` rounds money to two decimals, half away from zero, on its decimal value (Issue I-39); the examples of `uae.wps.table.returns.v1` prove both. It compiles to what the NRules canonical model expects.

Numbers are exact decimals (R-04-23). A literal is read by its text and a value as Part 2 says (R-02-17). `+`, `-`, `*`, `%`, the comparisons, `sum`, `sumWhere`, `min`, `max` and `round2` give the exact decimal result, so the sum of 0.1, 0.2, 0.05, 0.2, 2697.81, 1395.44 and 3801.07 equals the control total 7894.87, which binary floating point gets wrong. `/` gives 20 decimal places, half away from zero. A comparison is numeric when either side is a number or the result of arithmetic; two strings compare as text, so codes with leading zeros stay codes. `+` of two strings that are both decimals adds them; any other two strings are joined (use `concat` for text). A result with at most 15 significant digits is a number; a longer one is a decimal string. A script-behind gets the same arithmetic on `rules.money` (`add`, `sub`, `mul`, `div`, `sum`, `round2`, `cmp`, `eq`, `str`). A rule needs no tolerance, so a rule that compares a difference with a small fraction (`total - b.controlSum < 0.01`) is refused (L42). The grammar is a deliverable of build spec C06.

## 4.4 Requirements

| Id | Requirement | Check |
| --- | --- | --- |
| R-04-01 | A rule MUST be declarative, side-effect free and deterministic. It MUST NOT read the clock; the evaluation date is a fact. | Rule service test (C06) |
| R-04-02 | A validation rule MUST yield exactly one finding. | Schema `rule` (L00); lint L12 |
| R-04-03 | A finding MUST carry severity (`reject`, `repair`, `warn`), a path and an ISO reason code. It MAY carry a scheme code and a message. | Schema (L00) |
| R-04-04 | A rule MUST bind to canonical paths. Only an edge rule named by a map may use `SourceFile`. | Review; lint L12 |
| R-04-05 | A rule MUST ship at least one passing and one failing example, and a boundary example for any threshold. | Schema (L00); publish check |
| R-04-06 | A rule that uses a `Param` fact MUST declare that parameter. | Lint L17 |
| R-04-07 | A parameter MUST declare type and default, and SHOULD declare bounds. | Schema `common#parameter` (L00) |
| R-04-08 | A value set in a lower scope MUST lie inside the bounds declared by the owner. A bank changes values, never the rule. | Lint L14 |
| R-04-09 | Rule logic is pinned with the workflow release. Parameter values are looked up by effective date at each step. | Conformance CK-LOOKUP-01 |
| R-04-10 | A rule MUST declare whether it reads values as of step time or as of the payment's business date. Step time is the default. | Schema `common#parameter.lookupDate` (L00) |
| R-04-11 | A dated change SHOULD be expressed as a value, not as a new rule release. | Review |
| R-04-12 | A rule set MUST declare its mode: `validation` or `decision`. | Schema (L00) |
| R-04-13 | In a validation set no rule may change working memory. Every finding of a stage is collected. | Lint L12; rule service test |
| R-04-14 | A validation set runs its stages in order and MUST NOT enter the next stage with a blocking finding. | Rule service test (C06) |
| R-04-15 | In a decision set a rule may yield facts that other rules match. The yield graph MUST be acyclic. | Lint L25 |
| R-04-16 | One rule session per rule-set call. Nothing survives the call. | Rule service test (C06) |
| R-04-17 | A decision table MUST declare a hit policy: `unique`, `first` or `collect`. A `first` table MUST end with a catch-all row. | Schema (L00); lint L25 |
| R-04-18 | The studio MUST report gaps and overlaps in a `unique` table. | Studio test (C13) |
| R-04-19 | A workflow branch MUST read a named outcome or a decision. It MUST NOT embed a condition on payment data. | Schema `workflow#switchStep` (L00) |
| R-04-20 | The trace of rules fired, inputs and releases MUST be stored with the payment. | Conformance CK-TRACE-01 |
| R-04-21 | Rules MUST run in one rule service on every engine and in the studio. | Architecture; conformance kit on all targets |
| R-04-22 | Each rule SHOULD link to the scheme clause it implements. | Publish report (advisory) |
| R-04-23 | Rule, table and script-helper arithmetic on numbers MUST be exact decimal arithmetic, with `round2` half away from zero and `/` to 20 places half away from zero, and MUST give the same result on the studio interpreter and every engine. A rule MUST NOT compare money with a floating-point tolerance. | Conformance CK-MONEY-01 on every target; lint L42; `studio/test/money.test.ts` |

## 4.5 Evaluation cycle

1. The calling activity builds facts from its declared reads, the looked-up parameter values and the evaluation date.
2. The session matches every rule. Each match is an activation.
3. Activations fire in priority order. A firing in a decision set may add facts, which may add activations.
4. The session ends when no activation is left.
5. Findings and decisions are returned; the activity derives its outcome from the worst severity or from the decision.

## 4.6 Examples

- Policy rule with a bounded parameter: `examples/bank/rules/b.rule.internal-limit.v1.yaml`.
- Cross-field batch rule with a query: `factory/uae.wps/rules/uae.wps.rule.control-total-matches.v1.yaml`.
- Decision table, `first` with a catch-all: `factory/p/rules/p.table.inward-disposition.v1.yaml`.
- A bank value that tightens a scheme limit from a date: `examples/bank/configuration-set.yaml`.

## 4.7 Open decisions touching this part

D13 (NRules as the evaluator on .NET), D16 (lookup date default).
