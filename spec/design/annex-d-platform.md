# Annex D. Platform and cluster platform content

Status: draft 0.1, 17 November 2026. The factory content of `p` and `uae.p`.

## D.1 Stages and blocks

| Lifecycle stage | Blocks in `p` | Blocks in `uae.p` |
| --- | --- | --- |
| Intake | `p.decide.workflow.v1`, run by the front door and not by a workflow | `uae.p.enrich.business-date.v1` |
| Check | `p.validate.canonical.v1`, `p.validate.duplicate.v1`, `p.validate.limits.v1` | `uae.p.validate.iban.v1`, `uae.p.enrich.participant.v1` |
| Screen | `p.screen.sanctions.v1`, `p.screen.fraud.v1`, `p.decide.inward-disposition.v1` | |
| Reserve | `p.reserve.funds.v1`, `p.reserve.release.v1` | |
| Route | `p.decide.rail.v1` | |
| Scheme leg | The `scheme-leg` slot | |
| Post, close | `p.post.accounting.v1`, `p.post.accounting.v2` (an order's converted amounts, charges and VAT, R-12-23), `p.post.verify.v1`, `p.post.reverse.v1`, `p.notify.channel.v1`, `p.notify.publish-fact.v1`, `p.reconcile.scheme-report.v1`, run on a schedule | |
| Any stage | `p.hold.repair.v1`, `p.hold.approval.v1`, `p.enrich.payment-record.v1` | |

A `p` workflow cannot name a cluster, so the `uae.p` blocks are called by the legs: `uae.p.enrich.business-date.v1` and `uae.p.validate.iban.v1` (per salary record) by `uae.wps.wf.salary-file-out.v1`. No leg calls `uae.p.enrich.participant.v1` now (Issue I-38 in `docs/ISSUES.md`). The `uae.p` rules are also reused one by one inside scheme rule sets. Both forms of reuse are deliberate: a block where the whole check applies, a rule where a scheme needs only part of it.

## D.2 How the north-star decisions show up

| North-star decision | In the factory content |
| --- | --- |
| Reserve funds in the hub | `p.reserve.funds.v1`, with `p.reserve.release.v1` as its compensation |
| Post with accounting templates | `p.post.accounting.v1`, parameter `template` |
| Check the posting really happened | `p.post.verify.v1`; a mismatch ends in `review`, never in an automatic reversal |
| A platform step chooses the rail and records the reason | `p.decide.rail.v1` over `p.table.rail-choice.v1`; the enabled rails are a bank value |
| A few approved emergency signals | `signals` on every platform workflow |
| Bank-neutral core | No bank value is a constant; each is a parameter with a default |
| Every connection is a versioned profile | Eight `p` ports and two `uae.p` ports, each with a simulator |

## D.3 Design choices worth reading before changing anything

| Choice | Reason |
| --- | --- |
| `finally` holds the notify steps and the `on-close` hook | A rejected payment must be notified too |
| Outward workflows end in `review` after the leg has settled, not in `rejected` with compensation | Money has left; releasing the reservation would be wrong |
| The inward workflow records check outcomes and carries on to a decide block | The scheme must be answered by a leg on every path |
| `p.wf.request.v1` has no reservation and no posting | A refund request or a recall is not a payment |
| `p.wf.refund.v1` and `p.wf.refund-out.v1` own a refund order's money | A refund is an order of the platform's in each direction: inward, the bank refunds what a scheme asks (it posts, then the scheme leg answers); outward, the bank asks a scheme to refund its customer (the scheme leg asks and reads the settlement, then the platform posts the credit). A scheme leg posts nothing (L47) |
| Hook points for check and screen live in the segment | Every workflow that uses the segment offers the same hook points |
| `p.validate.duplicate.v1` is an activity with a port, not a rule | A duplicate check is stateful |
| Screening runs sanctions and fraud in parallel | Neither depends on the other |

## D.4 Artefacts in `p`

Generated from `factory/inventory.yaml`.

<!-- INVENTORY:p -->
| Kind | Count | Artefacts |
| --- | --- | --- |
| Activity | 29 | `p.decide.await-posting-window.v1`, `p.decide.cut-off.v1`, `p.decide.inward-disposition.v1`, `p.decide.posting-window.v1`, `p.decide.rail.v1`, `p.decide.workflow.v1`, `p.enrich.charges.v1`, `p.enrich.fx-rate.v1`, `p.enrich.party-register.v1`, `p.enrich.payment-record.v1`, `p.hold.approval.v1`, `p.hold.repair.v1`, `p.notify.channel.v1`, `p.notify.publish-fact.v1`, `p.post.accounting.v1`, `p.post.accounting.v2`, `p.post.reverse.v1`, `p.post.verify.v1`, `p.protect.decrypt-file.v1`, `p.protect.encrypt-file.v1`, `p.reconcile.scheme-report.v1`, `p.reserve.funds.v1`, `p.reserve.release.v1`, `p.screen.fraud.v1`, `p.screen.sanctions.v1`, `p.validate.canonical.v1`, `p.validate.canonical.v2`, `p.validate.duplicate.v1`, `p.validate.limits.v1` |
| Rule | 8 | `p.rule.amount-positive.v1`, `p.rule.amount-within-platform-limit.v1`, `p.rule.batch-currency-present.v1`, `p.rule.creditor-account-present.v1`, `p.rule.creditor-town-country.v1`, `p.rule.currency-present.v1`, `p.rule.debtor-account-present.v1`, `p.rule.end-to-end-id-present.v1` |
| Ruleset | 2 | `p.ruleset.canonical.v1`, `p.ruleset.limits.v1` |
| Table | 3 | `p.table.inward-disposition.v1`, `p.table.rail-choice.v1`, `p.table.workflow-select.v1` |
| Port | 15 | `p.port.channel.v1`, `p.port.core-banking.v1`, `p.port.decryption.v1`, `p.port.encryption.v1`, `p.port.event-stream.v1`, `p.port.facts-out.v1`, `p.port.file-transfer.v1`, `p.port.fraud.v1`, `p.port.fx-rates.v1`, `p.port.liquidity.v1`, `p.port.notification.v1`, `p.port.operations-queue.v1`, `p.port.party-register.v1`, `p.port.payment-record.v1`, `p.port.sanctions.v1` |
| Simulator | 15 | `p.sim.channel.v1`, `p.sim.core-banking.v1`, `p.sim.decryption.v1`, `p.sim.encryption.v1`, `p.sim.event-stream.v1`, `p.sim.facts-out.v1`, `p.sim.file-transfer.v1`, `p.sim.fraud.v1`, `p.sim.fx-rates.v1`, `p.sim.liquidity.v1`, `p.sim.notification.v1`, `p.sim.operations.v1`, `p.sim.party-register.v1`, `p.sim.payment-record.v1`, `p.sim.sanctions.v1` |
| Workflow | 8 | `p.seg.check-and-screen.v1`, `p.sub.await-posting-window.v1`, `p.wf.commercial.v1`, `p.wf.corporate.v1`, `p.wf.inward.v2`, `p.wf.refund-out.v1`, `p.wf.refund.v1`, `p.wf.request.v1` |
| Test | 23 | `P-CHG-001`, `P-CHG-002`, `P-DEC-001`, `P-DEC-002`, `P-ENC-001`, `P-ENC-002`, `P-FX-001`, `P-FX-002`, `P-FX-003`, `P-FX-004`, `P-FX-005`, `P-FX-006`, `P-FX-007`, `P-POST-001`, `P-POST-002`, `P-POST-003`, `P-POST-004`, `P-REG-001`, `P-REG-002`, `P-REG-003`, `P-REG-004`, `P-RES-001`, `P-RES-002` |
| Total | 106 | |
<!-- /INVENTORY:p -->

## D.5 Artefacts in `uae.p`

<!-- INVENTORY:uae.p -->
| Kind | Count | Artefacts |
| --- | --- | --- |
| Activity | 3 | `uae.p.enrich.business-date.v1`, `uae.p.enrich.participant.v1`, `uae.p.validate.iban.v1` |
| Rule | 5 | `uae.p.rule.currency-aed.v1`, `uae.p.rule.iban-checksum.v1`, `uae.p.rule.iban-country.v1`, `uae.p.rule.iban-length.v1`, `uae.p.rule.routing-code-format.v1` |
| Ruleset | 1 | `uae.p.ruleset.iban.v1` |
| Port | 2 | `uae.p.port.calendar.v1`, `uae.p.port.participant-directory.v1` |
| Simulator | 2 | `uae.p.sim.calendar.v1`, `uae.p.sim.participant-directory.v1` |
| Total | 18 | |
<!-- /INVENTORY:uae.p -->

## D.6 Open points

| Id | Point |
| --- | --- |
| OP-D1 | After a repair is released, the payment is not validated again. A bounded loop around validate would close that. |
| OP-D2 | `p.table.rail-choice.v1` only honours the requested rail if the bank has enabled it. Choice by amount, time, cost and cut-off is a bank's rail order, to be modelled as rows supplied by configuration. |
| OP-D3 | Corporate and commercial differ only in budgets, the deadline and the synchronous marker. If they stay that close, one workflow with parameters may be enough. |
| OP-D4 | Segment and cluster codes for `header.segment` and `header.scheme` need a registry. |
| OP-D5 | A liquidity or prefund block at platform level was removed because no leg used it. Add it back with the first rail that needs it. |
