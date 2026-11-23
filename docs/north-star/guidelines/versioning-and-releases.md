# Versions, releases and hot update

How a change reaches payments. It replaces the source's Temporal versioning instructions, its governance lifecycle and its hot-reload rules. The rules are in `spec/CLAUDE.md` (Versions), Part 1 and Part 11 of the spec, and plan W4.

## What the source did, and why it is not carried

The source ran one generic workflow and guarded each behaviour change with a nested `Patched` marker that could never be renamed or removed. Activity code was hot-reloaded and replaced the code running in flight. Definitions could still be edited before production and after deprecation (N9). On DTFx the patch check always answered true (N2). None of this is carried.

## The rules

| Rule | Source | Checked by |
| --- | --- | --- |
| A name carries its major: `<scope>.<type>.<name>.v<major>`. A release is `major.minor.patch`: an interface change is a major, an improvement a minor, a fix a patch. | `spec/CLAUDE.md` | Interface diff and proposed release in the studio (C05) |
| A published release is never edited. Release a new one. | `spec/CLAUDE.md`; R-09-21 | `content.test.ts`; the release API refuses the same id with another hash |
| Pins are exact: name, release and hash. Never a range. | `spec/CLAUDE.md` | Pins and manifests (C05) |
| Logic is pinned to the payment's release and does not change in flight. | `spec/CLAUDE.md`; plan W4 | Kit scenarios on pins and supersession |
| Values, lists and registries are looked up by date and reach payments in flight. | `spec/CLAUDE.md` | Lookup tests; `StoreTests.A_payment_asking_as_of_its_acceptance_gets_the_same_answer_after_a_hot_update` |
| A rule version is chosen by date first. | Plan W4 (bug N1) | `StoreTests.A_future_dated_rule_does_not_hide_the_rule_in_force` |
| An activation is a new row. Rollback is a new activation pointing at an earlier release. | Plan W4 | `StoreTests.New_payments_take_the_active_release_and_rollback_is_a_new_activation` |
| A test package never reaches production. A package with simulators is not promotable. | `spec/CLAUDE.md`; studio README | `ReleaseApiTests.Activation_picks_what_new_payments_take_and_a_test_package_never_reaches_production` |

## How a change travels

1. A designer drafts in the studio. Each save is a commit on the change set's branch.
2. A checker who is not the maker approves. The studio publishes the change set as one commit on the content branch, or opens a pull request in pull-request mode (D8).
3. A workflow release is exported per engine target into `content-configurations/releases/`, written once.
4. A pipeline on the branch stores each new release in the runtime, through `POST /import-branch` or `POST /releases` (plan W8). The API is built; the pipeline is target.
5. Someone records an activation (`POST /activations`): from that time new payments take the release. Payments already running finish on theirs.

A rule or configuration value change follows steps 1 and 2 only, and approval builds the bank's signed configuration release. It reaches payments by its effective date, with no deployment, once the service console installs it with a bank approver's consent, or once a bank approver consents to the release the pipeline installed (`POST /sources/install`; at once only in a test environment, R-09-111). A branch's folders bring no values (R-09-110).

## Running payments

A running payment changes only through declared operator actions: retry, requeue, repair, cancel, decide (plan W4, R-05-50, R-09-91). There is no `InjectState` and no free key-and-value edit. Who may take an action, and where: the operations console (`design/operations-console.md`).

## Activity code

- An activity is versioned as `name@release`, with the checksum of its code or script in the package manifest.
- A worker refuses a task that is not on its queue.
- Workers keep old versions loaded while a payment pinned to them is open: the engine host runs a host per distribution, side by side, and on Temporal each distribution is a version of the bank's worker deployment whose build id is the distribution's id (R-09-66 to R-09-68). A distribution drains, and is removed only once none of its payments is open (`docs/runtime/PACKAGING-AND-DEPLOYMENT.md`).

## Supersession

When a payment in flight must move to a later release, that is a supersession: explicit, reviewed, and handed to operations (C19, D15). It is not a patch.
