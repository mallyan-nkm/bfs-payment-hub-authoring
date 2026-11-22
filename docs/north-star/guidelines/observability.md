# Observability

What a runtime host, engine host or the studio tells an operator about itself. It replaces the source's logging instructions, observability agent and skills, and its log catalogue.

**Status.** Not decided for the north star: [I-66](../../ISSUES.md#i-66-observability-of-the-runtime-and-the-studio). The runtime has no traces, metrics or structured logging today, and no health endpoint but the native host's `GET /health`. Do not build a telemetry pipeline, choose a backend or name one in shared code until I-66 is decided.

## What already holds

These rules come from `spec/CLAUDE.md` and the plan, and apply to any log or trace written now:

- **No personal data** in a log, a trace, an exception message, a test, a sample or a commit message. Log a payment's id and release, never a party's name, account, IBAN, identity number, email or phone. This is the source's rule too, and its privacy scan found it broken in places (PV-001).
- **The business trace is the record of a run.** The studio's run record keeps per instance the business trace, the release trace, each step with its attempts and policy, the delta it wrote and the release that ran (`studio/README.md`, "How a run works"). Engines produce the same trace; the kit compares it.
- **Engine history holds ids, not data.** `PaymentContext` carries references only (plan W1). The plan asks for a test that scans each engine's history for any value from the party records.
- **The fact chain is the audit.** What happened to a payment is its facts, hash-chained in the store. Telemetry is for operating the system; it is never the legal record.

## What the source did that is worth keeping when I-66 is decided

These are patterns, not decisions. They are listed so the decision does not have to rediscover them.

- One trace per payment across hosts, with the trace id carried into the facts as a correlation reference.
- Attribute names that are lower case and dotted, the same in every host, with the tenant and scheme as dimensions.
- Metrics by the RED method for request paths (rate, errors, duration) and by the USE method for resources (utilisation, saturation, errors).
- Structured logs with named parameters, never string interpolation of values into the message.
- Telemetry that never blocks the work: a host keeps running when the collector is down.
- Health, liveness and readiness endpoints on every host.

The source's own service names, metric prefixes, collector configuration and backend are not carried.
