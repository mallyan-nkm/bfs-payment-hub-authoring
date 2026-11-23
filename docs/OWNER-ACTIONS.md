# What is needed from the owner, and from others

Status: 23 November 2026.

**Owner's decisions of 23 November 2026:** every recommendation in part A is accepted, except A4 (crypto-shredding), which is deferred. In addition, the owner asked for a notification port with an MQTT adapter and an event-streaming port with a Kafka adapter (A5). Building is under way.
 Each item says what it is in plain words, why it matters, what I recommend, and the issue in `docs/ISSUES.md` that holds the detail. Nothing here blocks the laptop stack or the tests; these items block a real bank going live.

## A. Decisions only the owner can make

### A1. Fix what the north-star review found (three pieces of work)
**What it is.** An independent review of the code against the ten north-star points (23 November 2026) found five serious gaps:
- **Values bypass the controls.** A folder of bank values can be loaded into the runtime with no signature and no bank approver's consent (`POST /import-branch`).
- **Native code isn't packaged.** The native code (the C# activities, WPS among them) is built into the runtime's image, not carried in each distribution. Two distributions running side by side therefore share one copy of it.
- **The engine host ignores the bank's values.** It runs with no bank values, so settings such as the acknowledgement wait fall back to the factory default.
- **Configuration installs skip consent.** A configuration release installed through the release API goes into force without consent.
- **Connection settings are read once.** Port bindings are read once at start from a folder, not from the installed configuration release.

**Why it matters.** The first and fourth break "every system change needs the bank approver's consent". The second breaks "a new distribution is a complete, independent copy". The third means a bank's setting is silently not used.

**Recommendation.** Build all three pieces:
1. **Values and consent:** values arrive only in signed configuration releases, checked against their bounds, waiting for consent; bindings are read from the installed release.
2. **Native code in the distribution:** each distribution carries its native assemblies, checked by hash and loaded side by side.
3. **Engine host:** values are read as of each step's date; one worker per distribution; two activations at once cannot both win; files go through the File API by default; and the laptop stack gets a PostgreSQL version.

**Done for piece 1 (23 November 2026).** Values arrive only in signed configuration releases, checked against every distribution's bounds and put in force on a bank approver's consent (R-09-110, R-09-111); the release API's configuration installs wait for that consent outside its test environments; every host reads its bindings from the configuration release in force, with no restart (R-09-112, R-09-113); a backup holds its own bank only (R-09-116); workflow packages are refused beside a distribution (R-09-114). Issues I-210 to I-214 say what is left: the laptop stack must now install its bank's configuration release (I-213), and a distribution's install through the release API is not yet held to consent (I-211).

### A2. Capabilities of the original platform with no home (I-189)
The original had these; nothing in the new design covers them yet.

| Capability | In plain words | Recommendation |
| --- | --- | --- |
| Exchange-rate feed | The original refreshed exchange rates on a schedule and warned when they were old. Today a bank types its rates in as a value, and nothing says when they are stale. | **Build it.** Wrong rates mean wrong amounts. A small scheduled job that publishes a configuration release, plus a "stale" warning. |
| Fraud alerts and cases | The original listed fraud alerts and let staff work them as cases. Today a suspect payment is held or refused, and that is all. | **Build a simple alert list** in the operations console, using the work queue. Full case management can wait. |
| Circuit breakers | The original stopped calling a failing connection for a while. Today it keeps retrying. | **Build it** into the adapters. It is small and protects core banking. |
| Customer and counterparty 360 | One screen with everything about one customer. | **Defer.** Useful, but not needed to move payments. |
| Personal workspace and notification centre | Widgets and in-app notices. | **Defer.** |
| Kubernetes portal | A view of the cluster inside the console. | **Drop.** Banks use their own cluster tools. |

**Done for the circuit breakers (23 November 2026).** Core banking, the gateways, SFTP, MQTT, Kafka and the webhooks each stop calling a failing system for a while, then try one call; the thresholds are settings with defaults and bounds, and an open breaker shows in the service console's health (R-09-155, R-09-156). Left: the File API's and the engine host's breakers are not yet shown there (I-255).

### A3. "Associated accounts" (I-184)
**What it is.** In the original, "associated accounts" meant the bank's own ledger accounts a WPS upload posts to (settlement, suspense, income, VAT). I built the corporate's own accounts from core banking.

**Recommendation.** Keep the corporate's accounts for channels. Add a read-only view of the bank's ledger accounts in the operations console for supervisors. Those accounts are bank settings, so they should not be offered to channels.

### A4. Erasure that removes the bytes (I-180)
**What it is.** An erased payment now shows `[erased]` everywhere, but the original values are still inside the database rows, kept files and backups, because the store never deletes.

**Decided: deferred.** **Recommendation (kept for later).** Before the first bank goes live, add "crypto-shredding": each payment's personal data is encrypted with its own key in the bank's key vault, and erasing destroys the key. This needs each bank to provide a key vault.

### A5. A platform notification port (I-182)
**What it is.** Turnaround alerts go out through the operations console's own small adapter (a log or a webhook), not through a platform port the bank binds like any other.

**Recommendation.** Add `p.port.notification.v1` to the platform, so each bank decides where alerts go in its configuration.

**Decided:** add `p.port.notification.v1` with an MQTT adapter, and an event-streaming port (payment events out, for example to Kafka) with a Kafka adapter.

Also decide whether turnaround limits count business days or clock time. I recommend business days, as for hold expiry.

**Done (23 November 2026).** Factory 1.15.0 has both ports with simulators. Alerts go out by MQTT (or a webhook, or the log), the turnaround alerts among them; payment events go out to Kafka from the store's outbox, references only, at least once and in order per payment; turnaround limits count business days of the bank's calendar (R-05-80, R-05-81, R-09-150 to R-09-154). Each real bank's folder binds both by placeholder until it names its broker and cluster (B, I-163). The laptop has both brokers under the profile `events`. Left: I-256 to I-259.

### A6. Small clean-ups
- **I-154:** give "this is another bank's package" an error code of its own. Today it reuses `package.Rejected`. Recommendation: yes.
- **I-155:** drop the old role name `ServiceApprover` once role maps say `BankApprover`. Recommendation: yes, in the next runtime release.
- **I-190:** the spec text still says the conformance kit has 55 scenarios; it has 57. Recommendation: correct it the next time those spec parts change.

**Done (23 November 2026).** All three: `package.WrongBank` in the error dictionary 1.2.0 (R-09-117), `ServiceApprover` dropped (R-09-115), and the kit is the one place that counts its scenarios, held by lint L51 (R-09-118).

### A7. Decided on 23 November 2026, later the same day
- **I-191, the cut-off:** accepted as built. The check runs right after the business date is set. A late file moves to the next business day or is refused, as the bank's row says. A file that arrives on a closing day counts for the next business day.
- **I-205, licensing a scheme from the studio:** build it. The bank configurator proposes "license this scheme" and the bank approver approves.
- **I-206, a payment pinned but never started:** build it. It gets an "abandoned" end, and the service console lists stuck pins.
- **I-232, one worker set per distribution:** deferred.
- **Outbound files:** the File API becomes a port that sends data out, so a scheme's outgoing file leaves through the File API's outbox (I-192).
- Also to build: the upload page and the audit pages in the consoles, the file name read from the File API (I-195), the laptop's register maker, and the two studio tests that use old release numbers.

## B. Things to ask each bank
You pass these on; I cannot get them myself. Until each arrives, the bank's folder holds a placeholder, and the runtime refuses rather than guessing.

| Bank | What to ask for | Why | Issue |
| --- | --- | --- | --- |
| DIB | Its WPS settlement (nostro), suspense, charge income and VAT account numbers, and its charge slabs | Without them a WPS posting uses placeholder accounts that core banking does not know | I-58 |
| ADIB, ENBD, FAB | Their core-banking gateway settings (service code, host id, branch, paths, function ids) | Without them every core-banking call is refused | I-85 |
| ADIB | Its WPS routing code and CBX values, and whether `CB-NOSTRO-WPS` and the other three names are its ledger accounts | A salary file for ADIB is refused until the routing code is set | I-116 |
| Every bank | The host-key fingerprints of each SFTP partner (Central Bank, ministry, core banking), and a login per partner | The SFTP connection refuses any server whose key is not pinned | I-161 |
| Every bank | The interfaces of its sanctions engine, fraud engine, e-mail and SMS gateways, and the endpoint that receives payment events | The production connections are built, but each bank's systems speak their own form | I-163 |
| Every bank's security officer | Two settings: should core banking read the registers masked (`SystemReadsMasked`), and how long an unmasked value stays visible (`unmaskWindow`, at most 4 hours) | Both are off or open by default; the bank must choose | I-181 |
| Every bank's operations head | The turnaround limits, the retention period, its own closing days and its cut-off times (ADIB and DIB have no cut-off table yet) | All default to zero or empty: nothing breaches and nothing is erased until set | I-182, I-180, I-186 |
| Every bank's data protection officer | Approval to switch on the service console's AI assistant (it sends masked data to the model provider) | It stays off until approved | I-104 |
| Every bank's database administrators | Apply `deploy/laptop/sql/grants.sql`: the runtime's login may read and insert, never update or delete | That is what makes "insert, never update" hold at the database in production | I-157 |

## C. Things to ask the Central Bank or the WPS scheme operator

| What | Why | Issue |
| --- | --- | --- |
| The nine-digit routing codes of every bank | Only DIB's (802420101) is known. A salary line to another bank gets no bank name. | I-151 |
| Each year's holidays that follow the moon | The calendar only holds holidays on fixed dates | I-151 |
| The refund cycle (RRI, DCR, RRR): per line or per file; the meaning of the reason codes; what to do when a DCR or RRR is refused; whether the RRR's acknowledgement is awaited | Today the whole refund order is decided at once, and some answers are placeholders | I-134 |
| The layout of the reconciliation report, if the Central Bank sends one | Reconciliation reads a layout of the runtime's own until the scheme declares one | I-164 |

## D. Things you do yourself (GitHub and your machine)
1. **Real GitHub write test.** Run the studio once against the real content repository, with a token in `TH_GITHUB_TOKEN` (set in your shell, never in a file). Approve one change and see it land on the branch.
2. **Branch protection.** On GitHub, protect the content branch: no force-push, and no deletion.
3. **Make the repository private.** Do this as planned, once you're happy with it.
4. **Check the first CI run** on GitHub Actions, and tell me what failed.
5. **Laptop secrets.** Before `docker compose up`, export the passwords and secrets the README lists, including the new `TH_LAPTOP_ENGINE_HOST_TOKEN`.
6. **Docker clean-up permission.** The safety check here refuses broad Docker clean-ups, so when disk fills I'll ask you to run `docker volume prune -af` and `docker image prune -af`, as you did today.
