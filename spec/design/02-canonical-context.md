# Part 2. Canonical context

Status: draft 0.1, 17 November 2026. Normative words follow RFC 2119.

## 2.1 Purpose

After intake, every block reads and writes one context expressed in ISO 20022 element names and data types. Intake maps convert any source model into it. A scheme leg maps back out where its scheme needs a wire format. This part defines that context.

## 2.2 Structure

| Part | Holds | Basis |
| --- | --- | --- |
| `header` | Ids, kind, direction, segment, scheme, channel, dates, pinned releases | Business application header and group header fields |
| `payment` | One credit transfer: parties, accounts, agents, amount, purpose, remittance | Elements shared by pain.001 and pacs.008 |
| `batch` and `items[]` | A file or bulk: control data in `batch`, one entry per transaction in `items[]`, each with its own `payment` | Group and payment information levels |
| `status` | Current status, reason codes, history | pacs.002 and pain.002 status elements |
| `facts` | Block outputs that are not payment content | Not ISO; typed and registered per block |
| `ext.<scope>` | Fields with no ISO home, for example `ext.uae.wps`, `ext.b` | ISO data types; supplementary-data pattern |
| `messages` | References to raw inbound and outbound messages | Claim-check; no payload |

### Header fields

| Path | Type | Meaning |
| --- | --- | --- |
| `header.paymentId` | Max35Text | Platform id, assigned at intake. The root of every idempotency key. |
| `header.endToEndId` | Max35Text | End-to-end identification from the originator. |
| `header.uetr` | UUIDv4Identifier | Optional. Carried when the rail uses it. |
| `header.fileId` | Max35Text | File or batch id; the correlation key for file rails. |
| `header.kind` | Code | `payment`, `request`, `return` or `refund`. |
| `header.direction` | Code | `inward` or `outward`. |
| `header.segment` | Code | `corporate` or `commercial`. Extensible by registry. |
| `header.scheme` | Code | The scope of the scheme, for example `uae.wps`. Set at intake or by the route decision. |
| `header.channel` | Code | Originating channel. |
| `header.receivedAt` | ISODateTime | When the platform received the request. |
| `header.businessDate` | ISODate | The business date the payment runs on. |
| `header.tenant` | Max35Text | The bank tenant. Used for lookup, never for branching. |
| `header.originalPaymentId` | Max35Text | A refund order only: the id of the earlier payment it refers to (camt.056 Undrlyg/TxInf/OrgnlTxId). |

### Payment elements (minimum set)

`debtor`, `debtorAccount` (`iban`, `other`, `type`, `currency`), `debtorAgent` (`bicfi`, `clearingMemberId`, `name`), `creditor`, `creditorAccount` (`iban`, `other`, `type`, `proxy`), `creditorAgent`, `instructedAmount` (`value`, `currency`), `requestedExecutionDate`, `purpose`, `categoryPurpose`, `remittanceInformation`, `chargeBearer`. A party carries `name` and `postalAddress` with structured elements (`townName`, `country`, and the others ISO defines).

Batch control elements: `batch.numberOfTransactions`, `batch.controlSum`, `batch.currency`, `batch.categoryPurpose`, `batch.debtorAgent`, `batch.creditorAgent`.

### Amounts

An amount (`instructedAmount.value`, `controlSum`, an extension field of an ISO amount or decimal type, a parameter of type `amount` or `decimal`) is an exact decimal (Issue I-96). In canonical JSON it is either a decimal string, such as `"90071992547409.93"` (`^-?[0-9]+(\.[0-9]+)?$`), or a JSON number. Every reader takes a JSON number by its text, the shortest form that gives back the same binary value, so `0.1` is one tenth, never the binary value just above it. A number of at most 15 significant digits reads the same either way, so every document written before this rule keeps its meaning. A writer MUST write an amount of more than 15 significant digits as a decimal string, because a binary double cannot hold it: the studio's rule service, its script helpers and the interpreter do so, and an engine passes the text on untouched (R-09-47). Arithmetic on amounts is exact; it rounds only where a rule says so (`round2`, Part 4).
Batch party elements (Issue I-100): `batch.debtor` (PmtInf/Dbtr, the employer: `name`), `batch.debtorAccount` (PmtInf/DbtrAcct) and `batch.initiatingParty` (GrpHdr/InitgPty: `id` only). A file's source format may not carry them; the channel that uploaded the file declares them beside it, and the intake map reads them from what the channel declared.

An account's `type` is CashAccount40/Tp: an ISO `ExternalCashAccountType1Code`, or the scheme's own code. An agent's `name` is FinInstnId/Nm, looked up by its routing code.

Status of the lines (Issue I-100): `status.transactions[]` holds one entry per line a scheme's status report names (pain.002 TxInfAndSts): `lineNo`, `code` (the ISO status), `reasonCode` (ISO), `schemeCode` and `additionalInformation`.

Posting facts (Issue I-100): `facts.posting` carries the posting's entries in the shape of camt.054 (`entries[]`: `creditDebitIndicator`, `amount`, `currency`, the account masked, `branch`, `transactionCode`), the amount debited with charges (`totalAmount`) and its charges (`charges`: ChrgsInf, `amount`, `currency`, `tax` with `rate` and `amount`, and `totalChargesAndTaxAmount`). Amounts follow the decimal rule of amounts above.

## 2.3 Requirements

| Id | Requirement | Check |
| --- | --- | --- |
| R-02-01 | A context path MUST start with one of the seven parts in 2.2. | Schema `common#contextPath` (L00) |
| R-02-02 | Element names and data types in `payment`, `batch`, `items[]` and `status` MUST be those of ISO 20022. | Canonical model generator test (C03) |
| R-02-03 | Only a `map` activity may read or write a source format. Every other activity MUST declare its reads and writes as context paths. | Schema `activity` (L00) |
| R-02-04 | The canonical model MUST be message-neutral. A scheme message is a projection of it, defined by a map. | Review; map round-trip tests |
| R-02-05 | A path under `ext.` MUST fall under a registered extension root. | Lint L18 |
| R-02-06 | An extension field MUST declare an ISO data type, a level, a description and whether it is personal data. | Schema `extension` (L00) |
| R-02-07 | An extension MUST NOT repeat an element that ISO 20022 already defines. | Review at publish |
| R-02-08 | Engine state MUST carry only `header`, `facts` and references. The full context lives in the payment record. | Conformance CK-CLAIM-01 |
| R-02-09 | A field marked personal data MUST NOT appear in engine state, logs or traces. | Conformance CK-CLAIM-01 |
| R-02-10 | Each step's writes MUST be recorded as a delta against the context, with the step id and the release that ran. | Conformance CK-DELTA-01 |
| R-02-11 | Codes (purpose, category purpose, status, status reason) MUST come from versioned registries of ISO external code sets and scheme lists. | Registry test (C03) |
| R-02-12 | A script SHOULD NOT contain a literal code value; it reads the registry. | Script lint (advisory) |
| R-02-13 | A party's postal address SHOULD carry at least town name and country in their own elements. | Rule `p.rule.creditor-town-country.v1` (warn) |
| R-02-14 | `status` MUST use ISO payment status codes and ISO status reason codes; a scheme code MAY be carried beside the ISO code. | Rule finding schema (L00) |
| R-02-15 | A block that works on a batch or on an item MUST declare `level`. | Schema `activity` (L00) |
| R-02-16 | `header.tenant` MUST NOT be read by shared content to change behaviour. Tenant differences come from values and hooks. | Lint L09; review |
| R-02-17 | An amount MUST be an exact decimal: carried in canonical JSON as a decimal string or as a JSON number read by its text, never by its binary value. An amount of more than 15 significant digits MUST be a decimal string. | Lint L42; conformance CK-MONEY-01; `studio/test/money.test.ts` |
| R-02-18 | The initiating party of a file (`batch.initiatingParty`) MUST be carried by its id only, never by a name, and is personal data: it lives in the payment record and is masked wherever it is shown. A field the channel declared beside a file (initiator, debtor name and account, channel) MUST reach the context through the intake map, as the source format's fields do. | Canonical model (`studio/src/core/model.ts`, personal data); lint L38 (a view that shows it masks it); test WPS-MAP-003 |
| R-02-19 | A scheme status report that names lines MUST be read into `status.transactions[]`, one entry per line, each with an ISO status and, where the scheme gives one, its own code beside the ISO reason (R-02-14). | `WpsAckNakTests` in `runtime/tests/TransactHub.Activities.Wps.Tests` |
| R-02-20 | A refund order MUST carry `header.kind` refund and the id of the earlier payment it refers to in `header.originalPaymentId`; it is a new order, never a change to the earlier one. An account's currency, where the intake or core banking knows it, is `currency` on the account (CashAccount40/Ccy). | Canonical model (`studio/src/core/model.ts`); test WPS-RRI-001 |
| R-02-21 | A party MAY carry its organisation's id and the scheme that issued it, `organisationId.id` and `organisationId.scheme` (Pty/Id/OrgId/Othr/Id and SchmeNm/Prtry), for example a salary file's employer by its labour ministry establishment id (scheme MOL). It is an organisation's id, not personal data; the bank's party register is asked by it (R-12-27). | Canonical model (`studio/src/core/model.ts`); tests WPS-MAP-003, P-REG-001 |

## 2.4 The ISO data capability in the studio

| Capability | What the designer can do |
| --- | --- |
| Model browser | Search any element, type or code; see which blocks read and write it. |
| Message catalogue | See each scheme's messages with the scheme's restrictions. |
| Map editor | Define source to canonical and back; AI proposes, round-trip tests prove. |
| Sample generator | Produce valid and deliberately invalid messages per scheme. |
| Context diff | See the context before and after any step of a test run. |

## 2.5 Examples

- Extension registry entry: `factory/uae.wps/extensions/uae.wps.ext.salary.v1.yaml`.
- A map with field mappings: `factory/uae.wps/activities/uae.wps.map.sif-in.v1.yaml`.

## 2.6 Notes

Swift requires at least town and country in designated fields for parties in CBPR+ payment messages. Swift revised its SR2026 timeline in August 2026; confirm the live date before R-02-13 is raised from SHOULD to MUST.
