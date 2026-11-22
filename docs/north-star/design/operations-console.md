# The operations console

Operators see and act on running payments in the runtime, never in the studio (R-10-01). In the new architecture a scheme is a leg, so the console is leg-generic: it has no code for any scheme, and shows each leg as its pack's operations view declares it (R-05-48). Requirements: R-05-48 to R-05-50, R-06-22. Owner's decision: 2026-11-22 (I-65).

## The picture

```
 engines (Temporal, DTFx, Elsa, Flowable)                    the bank's identity provider (OIDC)
   │  every attempt a worker answers                                  │ code + PKCE        │ JWT
   ▼                                                                  ▼                    ▼
 WorkerRouter.Answered ─► StepFacts ─┐                         runtime/ops-console ──► TransactHub.Operations.Api
 claim check (payload facts) ────────┤                           (React, any view)       │  role mapping per tenant
 native host: holds (StoreHold) ─────┤                                                   │  OperatorActions ──┐
 operator actions ───────────────────┤                                                   │                    │ facts
                                     ▼                                                   ▼                    │
                        store: payment_fact ─► outbox (same save)  ──►  Projector ──► operations read model    │
                        (insert-only, hash-chained)    ▲                (checkpoint,    (ops_* tables, EF Core) │
                                     ▲                 │                 by chain no.)                          │
                                     └─────────────────┴───────────────────────────────────────────────────────┘
```

## The write side: what becomes a fact

| Fact (kind, code) | Written by | Data (never the payload) |
| --- | --- | --- |
| `status`, `RCVD` | `PaymentStore.AcceptBulk`, `AcceptPayment` | none |
| `step`, the outcome or `error` | `StepFacts`, from every engine's worker router | instance, step (with the composite's inner step), activity, release, call, attempt, level, the header's ids, the ISO status the state carried, reason code, the step's state writes (facts, status, message references), or the error's name |
| `payload`, `create` / `staged` / `commit` / `fork` / `copy` / `accepted` | the claim check (`DurableClaimCheck`, `StoreJournal`) | the payload: the projection reads only the lines' own facts (`items[i].facts`) from it |
| `hold`, `opened` / `decided` / `expired` | `StoreHold` (the native host), `OperatorActions` | hold id, kind, activity, reason, decision, who decided |
| `operator`, `requested` / `approved` / `rejected` / `done` / `refused` | `OperatorActions` | action id and name, remarks, the hold, the second approver's role, the result; the actor is `operator:<subject>` from the identity provider |
| `message`, e.g. `sif-generated` | the native SIF render | the message's id and hash |

`FactLog.Append` adds an outbox row (topic `fact`, the subject and number, no data) in the same save as the fact, and `PaymentStore.KeepMessage` one for each message (topic `message`). So the outbox misses nothing that was written and announces nothing that was not.

## The read side: the projection

- **Ordered.** The projector reads the outbox after its checkpoint, in id order, a batch at a time.
- **Idempotent.** Each fact stream (a subject's chain) is applied by number: a fact at or below the stream's last number is skipped; one above it first brings in the ones before it from the store. A replay changes nothing (`ProjectionTests`).
- **Resumable.** The checkpoint is the last outbox id through which everything is applied. A missing id (a write still in flight) holds it back until the id arrives, or until a later row is older than the gap timeout (10 s), when it is taken for a write that was rolled back. The rows after a gap are applied at once all the same, since applying is by number.
- **Rebuildable.** `Rebuild` deletes the read model and replays every fact and message from the store, then follows the outbox from rows older than the gap timeout. Rebuilt and incremental read models are equal, row for row, on both databases (`ProjectionTests`). `POST /ops/api/rebuild` is a supervisor's.
- **Lag.** `GET /ops/api/health` gives the outbox rows behind the checkpoint, the age of the oldest, the facts applied, and when the last was written and projected. The console shows it in its top bar.
- **Neutral.** `OpsDbContext` is EF Core with portable types, its own migrations per provider (`TransactHub.Operations.Migrations.SqlServer`, `…PostgreSql`) and its own history table; the provider is chosen in `StoreDbContext.ConfigureFor`, the one registration place. It may share the store's database or have its own (`Operations:Connection`). Unlike the store it is updated in place: it is not the record.

| Table | One row per |
| --- | --- |
| `ops_subject` | payment or bulk: tenant, scheme, direction, ids, file name, amount, count, ISO status, last step and outcome, hold and its reason, the views' subject fields |
| `ops_leg` | engine instance a leg of the scheme's view matched (`selectBy`): its leg, last step and outcome, the leg's fields |
| `ops_line` | line of a bulk: its own facts and the view's line fields, masked |
| `ops_field_value` | declared field value, as text and number, for the declared filters |
| `ops_timeline` | fact of the payment's chain, with its data |
| `ops_hold`, `ops_action` | hold, operator action |
| `ops_document` | message or file, by reference; its content stays in the store |
| `ops_stream`, `ops_checkpoint` | fact stream's last number; the projection's place in the outbox |

## Leg-generic detail: the operations view

A pack declares an `operations-view` artefact (schema `spec/schemas/operations-view.schema.json`, Part 5 section 5.10): the platform's in `p` (`p.view.operations.v1`) and each scheme's in its scope (`uae.wps.view.operations.v1`). It declares fields with a source, label, group, format and mask; list columns and filters; a bulk's lines; documents by message kind; and actions with their role, second approver, remarks and when they apply. Each leg says how its engine instance is told apart (`selectBy`: direction, kind, steps only it has), so a payment with several legs (a slot leg and a refuse leg, a platform with two slots) shows each one. Lint L37 checks a view against the flows it names; mutation cases M38 to M44 prove the check.

The WPS view covers every field the source's WPS screens showed that the new model has: `docs/runtime/ops-console-wps-mapping.md`, checked by `OperationsViewTests`. The fields with no source are Issue I-100.

## Operator actions

| Action | Path to the payment | Maker-checker in the factory's views |
| --- | --- | --- |
| approve, decline (an approval hold) | the hold's port decision: a `hold decided` fact the waiting hold reads (I-98) | no |
| release (a repair hold) | the same | a supervisor approves |
| reject (a repair hold) | the same | no |
| cancel | an `engine-signal` outbox row for the engine host (I-99) | a supervisor approves |
| re-screen | the same | no |
| retry, resend | recorded and refused: no engine path (I-99) | resend: a supervisor approves |

`OperatorActions` offers only what the views declare and the payment's state allows (an open hold of the right kind, the legs it has), checks the role in the payment's tenant, records the request, and carries the action out at once or, with a `secondApprover`, when another person with that role approves; the maker may not approve their own.

## Who may: OIDC and roles

- The console signs in at the bank's identity provider with the authorisation code flow and PKCE (`runtime/ops-console/src/shell/auth.ts`). The API validates the bearer JWT (issuer, audience, signature, lifetime) and refuses a token of the studio's issuer. The runtime keeps no identity store.
- Roles come from the bank's claims or groups through `Operations:Roles` (`RoleMapping`): Operator and Supervisor per tenant. An operator sees and acts on their tenants' payments only.
- Dev mode (`TH_AUTH_DEV=1`) takes a synthetic identity from a header and is refused when the host runs in Production (`OperatorApiTests`).
- The seam for the runtime's own authentication library is `OperatorAuth.AddOperatorAuthentication` (it registers the scheme) and `IOperatorIdentityResolver` (claims to roles). The web app's shell (`src/shell/`) is shared: the test bench can be a second section of the same app with the same sign-in.

## Personal data

The read model holds the declared fields only, masked as declared (account and id values keep four characters at each end, as the source's screens did). A field that reads personal data must declare its mask; `none` only where the source's screen showed it whole, named in `oldScreen` (L37). The step facts carry no payload and no error text. A document's content is served from the store to the payment's operators, as the source's Raw File tab did. An unmask is not built (I-100).
