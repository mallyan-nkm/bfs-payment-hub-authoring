# Access control

Who may do what. It replaces the source's RBAC guidelines and agent, its IAM and RBAC designs, and its user-management screens.

There are two kinds of user, and they never mix:

- **Authors** work in the studio. They change content, never a running payment (R-10-01).
- **Operators** act on running payments in the runtime. Their surface, identity and permissions are not decided: [I-65](../../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it).

The source had one identity service, a permission catalogue in five stores, rail gates and field masks for both. None of it is carried. Its own security suite fails on that catalogue (N15 in the plan).

## The studio: decided and built

| Rule | Source | Checked by |
| --- | --- | --- |
| Roles are enforced on every write. | C17 | Studio governance and API tests |
| The roles are: shared designer, shared checker, bank designer, bank checker, auditor, service operations. A user belongs to one tenant; `owner` is the tenant of shared content. | `studio/src/governance/governance.ts` | Tenant isolation suite |
| A tenant sees and changes only its own content and the shared content it may read. | C17 | Tenant isolation suite |
| The checker of a change set is never the maker of any member. | R-11-19 | Publish tests |
| Users and roles are `governance/users.yaml` on the content branch, changed only through maker-checker. | I-44 | `content.test.ts` |
| Every event is audited, hash-chained, on the branch. A refusal is audited too. | C17; `content-configurations/README.md` | Audit tests |
| The GitHub token is read from the environment, never stored, shown, logged or audited. | `studio/README.md` | Review; `no-disk-write.test.ts` |

The studio has no sign-in yet: the acting user is chosen in the web app's header and sent with each request. A shared installation needs a verified identity first ([I-65](../../ISSUES.md#i-65-the-runtimes-operator-surface-and-who-may-use-it)).

What the source's user-management screens did, and where it went:

| Source screen | North star |
| --- | --- |
| Approval queue for admin changes | Maker-checker on change sets in the studio |
| Admin activity log | The studio's audit on the branch |
| GL accounts | Bank rule values in the bank's configuration (plan W6) |
| Rails | A rail is enabled in the bank's configuration set; the route decision passes only an enabled rail |
| System configuration | Configuration sets and effective-dated values |
| Users, roles, screens, departments, profile switching, unlocking | Studio users in `governance/users.yaml`; for operators, I-65 |

## The runtime: what holds until I-65 is decided

- The release API (`POST /releases`, `POST /activations`, `POST /import-branch`) has no authentication. It records the actor it is given. Reach it only from the pipeline's own network.
- A running payment changes only through declared operator actions: retry, repair, cancel, decide (plan W4). There is no free edit of state.
- Authorisation is server-side. A user interface may hide what a user cannot do, but it is never the control. This was the source's rule and it holds.
- A tenant comes from a verified identity, never from a request header or body a client sets.
- Personal data is masked in any response to an operator unless the operator holds a permission to see it, and seeing it is audited. The source's masking catalogue is a good starting list; which permission unmasks what is part of I-65.

Do not build a permission catalogue, a console or role screens for operators before I-65 is decided.
