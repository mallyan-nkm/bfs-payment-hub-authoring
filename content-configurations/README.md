# Content and configurations

What the studio publishes and what a bank configures (plan section 2, W4, W6, W8). The studio writes here when it is connected to this repository's branch; the runtime's pipeline reads it (`POST /import-branch`).

| Folder | What | Who writes it |
| --- | --- | --- |
| `releases/<scope>/<name>/<release>/<target>/` | A released package of a workflow, for one engine. Written once, never changed. | The studio, on publish |
| `artefacts/<scope>/<name>/<release>.yaml` | Each published document at its release. | The studio, on publish |
| `change-sets/<id>.yaml` | What was published together, by whom, approved by whom. | The studio, on publish |
| `environments/<env>/activations/` | From when a release is the one new payments take. | The studio, or operations |
| `banks/<bank>/` | A bank's configuration: rule values, hook bindings, port bindings, effective-dated. | The bank, through the studio's maker-checker |

Values here are synthetic, with one exception. By the owner's decision of 2026-11-21 (D5 as amended), DIB's real configuration lives here, in `banks/dib/`: its bank pack, its configuration set, its core-banking binding and its CBX values. Its `README.md` says where each value came from and what is not carried. Another bank's real values (its routing code, GL accounts, host ids) still live in the bank's own copy of this repository. No secret lives here, DIB's included: a secret is named by reference and set at deployment.

| Bank folder | What |
| --- | --- |
| `banks/example-bank/` | Synthetic. Every knob the runtime reads, with made-up values. Its pack is `spec/examples/bank/`. |
| `banks/dib/` | DIB's real configuration. The studio loads its pack with `--bank dib`. |
