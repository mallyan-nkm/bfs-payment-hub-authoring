# Personal data

Where personal data may be, and where it must not. It replaces the source's privacy scan results. The source's classes of data hold; its findings were about its own services and are not carried, though two of them shaped the north star.

## Classes

| Class | Examples | Where it may live |
| --- | --- | --- |
| Identifying | Name, identity number, address, phone, email | The store's party rows, and the raw message rows that carried it |
| Financial | Account number, IBAN, amounts per person | The store's account, payment and message rows |
| Reference | Payment id, bulk id, end-to-end id, release, tenant | Anywhere, including workflow context, engine history, facts, logs and tests |

## Rules

| Rule | Source | Checked by |
| --- | --- | --- |
| Workflow state and engine history hold references only. | `spec/CLAUDE.md`, Data; plan W1 | `PaymentContext` shape; the history scan of plan W1 is target |
| No personal data in logs, tests, samples, prompts or commit messages. Use synthetic values. | `spec/CLAUDE.md` | Review |
| The studio holds no payment and no personal data. | R-10-01 | Architecture: it has no connection to a payment |
| Samples in the studio are synthetic and proved against their scheme's schema. | C12-S4 | Sample proofs |
| A bank's real values in its folder are configuration, never a customer's data. | D5 as amended | Review |
| Personal data shown to an operator is masked unless the operator may see it, and seeing it is audited. | Source rule, kept | Not built: [I-65](../../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it) |
| How long personal data is kept, and how it is removed from an insert-only store. | | Not decided: see below |

## What the source's findings changed

- **Full payloads in engine history.** The source put the whole upload, with emails, accounts and IBANs, into workflow variables, where they stay in history for good (N3). The north star's context carries ids, and activities read the store.
- **Personal data in structured logs.** The north star has no telemetry yet. When it does, no party value goes into an attribute or a message ([I-66](../../ISSUES.md#i-66-observability-of-the-runtime-and-the-studio)).

## Not decided

An insert-only store and a right to erasure pull against each other. The usual answer is to keep personal data only in party rows, encrypted with a key per party, and erase by destroying the key. That is not decided for the north star and touches [I-67](../../ISSUES.md#i-67-secrets-keys-and-file-encryption-at-deployment). Raise it there, with the retention periods a bank must keep by law, before building it.
