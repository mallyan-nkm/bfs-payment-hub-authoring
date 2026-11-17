# Annex B. Aani authored on paper

Status: draft 0.1, 17 November 2026. Provisional: AEP publishes Aani's design principles but not its message set. Message identifiers, time limits and reason codes here follow common ISO 20022 instant-payment practice. Confirm every item against the AEP participant specification (decision D10).

## B.1 The scheme in one paragraph

Aani is the UAE instant payments platform operated by Al Etihad Payments. It runs on ISO 20022, all day, every day. Transfers are capped at AED 50,000, a limit AEP has said it will review. Each transfer settles in real time on a gross basis against a prefunded amount. Participants are direct, or indirect through a sponsoring settlement bank. Overlay services include payment to a mobile number or email, QR payments and request to pay.

## B.2 What Aani tests in the model

| Model feature | Why Aani needs it |
| --- | --- |
| Thin maps | The wire is ISO 20022; a map is a restriction of canonical, not a translation |
| Short budgets and a deadline | An instant payment has seconds |
| The unknown-outcome path | A send that times out is followed by a status inquiry, never a blind re-send |
| An effective-dated value | The limit is a parameter; a bank may lower it from a date |
| A prefund check | Settlement is against a prefunded amount |
| Inward answers | The bank must answer the scheme for every inward credit |
| A refuse slot that answers | When the platform refuses an inward credit, a leg still replies |
| Handlers | An inward status inquiry and a proxy lookup are stateless |
| The `synchronous` marker | Whether every step is a durable task is open (D9) |

## B.3 Legs and handlers

| Artefact | Fills | Steps |
| --- | --- | --- |
| `uae.aani.wf.credit-out.v1` | `scheme-leg` of `p.wf.commercial.v1` | Scheme checks; participant lookup in the cluster directory; prefund check; render; send. On pending, await the status report and read it. On unknown, up to three status inquiries; end by the last inquiry |
| `uae.aani.wf.credit-in.v1` | `scheme-leg` of `p.wf.inward.v1` | Scheme checks; credit; render the status; answer. Each refusal path answers too. |
| `uae.aani.wf.credit-refuse.v1` | `scheme-refuse` of `p.wf.inward.v1` | Render the rejection; answer |
| `uae.aani.handler.status-inquiry-in.v1` | Handler | Read the request; read the payment record; render; answer |
| `uae.aani.handler.proxy-lookup.v1` | Handler | Resolve the proxy for a channel |

## B.4 Dispatch table

| Message | Assumed ISO message | Direction | Disposition |
| --- | --- | --- | --- |
| `uae.aani.credit-transfer` | pacs.008 | Out | Sent by `send-credit` |
| `uae.aani.status-report` | pacs.002 | In | Deliver by original end-to-end id |
| `uae.aani.status-request` | pacs.028 | Out | Sent by `inquire-status` |
| `uae.aani.credit-transfer-in` | pacs.008 | In | Start: payment, `credit-in`; refuse leg `credit-refuse` |
| `uae.aani.status-report-out` | pacs.002 | Out | Sent by `respond-status` |
| `uae.aani.status-request-in` | pacs.028 | In | Handler `status-inquiry-in` |
| `uae.aani.proxy-request` | Not known | Out | Sent by `proxy-resolve` |
| `uae.aani.proxy-lookup` | Not applicable | In, from a channel | Handler `proxy-lookup` |
| `uae.aani.return` | pacs.004 | Both | Planned |
| `uae.aani.recall-request` | camt.056, answered by camt.029 | Both | Planned |
| `uae.aani.request-to-pay` | pain.013, answered by pain.014 | Both | Planned |

## B.5 Rules

| Rule | Kind | Note |
| --- | --- | --- |
| `uae.aani.rule.amount-within-limit.v1` | Policy | Parameter `maxAmount`, default 50000 AED, upper bound 50000. A bank may only lower it. |
| `uae.aani.rule.creditor-iban-or-proxy.v1` | Field | An IBAN or a proxy must identify the creditor |
| `uae.p.rule.currency-aed.v1`, the three `uae.p` IBAN rules | Field | Reused from the cluster platform |

The example bank lowers `maxAmount` to 25000 from 1 January 2027 (`examples/bank/configuration-set.yaml`). Tests `AANI-RULE-003` and `AANI-RULE-004` prove the change by date.

## B.6 Artefacts

Generated from `factory/inventory.yaml`.

<!-- INVENTORY:uae.aani -->
| Kind | Count | Artefacts |
| --- | --- | --- |
| Activity | 11 | `uae.aani.enrich.proxy-resolve.v1`, `uae.aani.exchange.inquire-status.v1`, `uae.aani.exchange.respond-status.v1`, `uae.aani.exchange.send-credit.v1`, `uae.aani.map.credit-in.v1`, `uae.aani.map.credit-out.v1`, `uae.aani.map.status-in.v1`, `uae.aani.map.status-out.v1`, `uae.aani.map.status-request-in.v1`, `uae.aani.reserve.prefund-check.v1`, `uae.aani.validate.credit.v1` |
| Rule | 2 | `uae.aani.rule.amount-within-limit.v1`, `uae.aani.rule.creditor-iban-or-proxy.v1` |
| Ruleset | 1 | `uae.aani.ruleset.credit.v1` |
| Port | 2 | `uae.aani.port.proxy.v1`, `uae.aani.port.switch.v1` |
| Simulator | 3 | `uae.aani.sim.proxy.v1`, `uae.aani.sim.switch-inward.v1`, `uae.aani.sim.switch-outward.v1` |
| Workflow | 5 | `uae.aani.handler.proxy-lookup.v1`, `uae.aani.handler.status-inquiry-in.v1`, `uae.aani.wf.credit-in.v1`, `uae.aani.wf.credit-out.v1`, `uae.aani.wf.credit-refuse.v1` |
| Scheme | 1 | `uae.aani.scheme` |
| Test | 14 | `AANI-HND-001`, `AANI-IN-001`, `AANI-OUT-001`, `AANI-OUT-002`, `AANI-OUT-003`, `AANI-OUT-004`, `AANI-OUT-005`, `AANI-OUT-006`, `AANI-OUT-007`, `AANI-OUT-008`, `AANI-RULE-001`, `AANI-RULE-002`, `AANI-RULE-003`, `AANI-RULE-004` |
| Total | 39 | |
<!-- /INVENTORY:uae.aani -->

## B.7 Open points

| Id | Point |
| --- | --- |
| OP-B1 | The message set, versions and usage guidelines are assumed. |
| OP-B2 | The end-to-end time limit and the inward answer time limit are placeholders (PT8S, PT5S). |
| OP-B3 | Whether the bank answers an inward credit before or after it credits the account is a scheme rule. The leg credits first. |
| OP-B4 | Proxy resolution is assumed to happen in the channel, before the payment order, so that screening sees the resolved creditor. If the bank must resolve inside the payment, `p.wf.commercial.v1` needs an enrich slot before screening. |
| OP-B5 | Direct and indirect participation is treated as configuration of the settlement path, not as a second leg. |
| OP-B6 | QR payments are treated as one more intake map; none is defined yet. |
| OP-B7 | Whether every step is a durable task or the synchronous part takes a faster path is decision D9. |
