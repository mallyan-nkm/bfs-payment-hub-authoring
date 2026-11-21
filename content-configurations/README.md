# Content and configurations

What the studio publishes and what a bank configures (plan section 2, W4, W6, W8). The studio writes here when it is connected to this repository's branch; the runtime's pipeline reads it (`POST /import-branch`).

| Folder | What | Who writes it |
| --- | --- | --- |
| `releases/<scope>/<name>/<release>/<target>/` | A released package of a workflow, for one engine. Written once, never changed. | The studio, on publish |
| `artefacts/<scope>/<name>/<release>.yaml` | Each published document at its release. | The studio, on publish |
| `change-sets/<id>.yaml` | What was published together, by whom, approved by whom. | The studio, on publish |
| `environments/<env>/activations/` | From when a release is the one new payments take. | The studio, or operations |
| `banks/<bank>/` | A bank's configuration: rule values, hook bindings, port bindings, effective-dated. | The bank, through the studio's maker-checker |

Values here are synthetic. A bank's real values (its routing code, GL accounts, host ids) live in the bank's own copy of this repository, never in the shared one (decision D5).
