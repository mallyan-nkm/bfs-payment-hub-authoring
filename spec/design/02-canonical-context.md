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
| `header.kind` | Code | `payment`, `request` or `return`. |
| `header.direction` | Code | `inward` or `outward`. |
| `header.segment` | Code | `corporate` or `commercial`. Extensible by registry. |
| `header.scheme` | Code | The scope of the scheme, for example `uae.aani`. Set at intake or by the route decision. |
| `header.channel` | Code | Originating channel. |
| `header.receivedAt` | ISODateTime | When the platform received the request. |
| `header.businessDate` | ISODate | The business date the payment runs on. |
| `header.tenant` | Max35Text | The bank tenant. Used for lookup, never for branching. |

### Payment elements (minimum set)

`debtor`, `debtorAccount` (`iban`, `other`), `debtorAgent` (`bicfi`, `clearingMemberId`), `creditor`, `creditorAccount` (`iban`, `other`, `proxy`), `creditorAgent`, `instructedAmount` (`value`, `currency`), `requestedExecutionDate`, `purpose`, `categoryPurpose`, `remittanceInformation`, `chargeBearer`. A party carries `name` and `postalAddress` with structured elements (`townName`, `country`, and the others ISO defines).

Batch control elements: `batch.numberOfTransactions`, `batch.controlSum`, `batch.currency`, `batch.categoryPurpose`, `batch.debtorAgent`, `batch.creditorAgent`.

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
