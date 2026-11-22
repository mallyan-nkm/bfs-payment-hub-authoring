# Encryption and secrets

How secrets are named and read, how released packages are signed, and how files are decrypted. It replaces the source's encryption guidelines and agent, in substance.

**Status.** Secrets by reference, package signing and the CI scans were decided by the owner on 2026-11-22 and are built ([I-67](../../ISSUES.md#i-67-secrets-keys-and-file-encryption-at-deployment)). Where decryption runs is still open (I-67, point 2), and the Azure Key Vault provider is not built ([I-73](../../ISSUES.md#i-73-the-azure-key-vault-secret-provider-is-not-built)).

## Secrets

- No secret is committed: not in code, configuration, a bank's folder, a sample, a test or a commit message. This repository is public. Test keys and tokens are made by the test that uses them and removed when it ends.
- A secret is named by reference, never by value (R-06-22):

| Reference | Where the value is | For |
| --- | --- | --- |
| `env:NAME` | A variable the deployment sets | Simple installations, CI |
| `file:/absolute/path` | A mounted secret file: a Kubernetes or Docker secret, a Vault agent's sink. `TH_SECRET_FILE_ROOTS` limits the folders it may read | Containers |
| `vault:mount/path#field` | A field of a HashiCorp Vault KV version 2 secret. `TH_VAULT_ADDR` names the Vault; its token is itself a reference in `TH_VAULT_TOKEN` (env: or file:); `TH_VAULT_NAMESPACE` if needed | Installations with Vault |
| `akv:…` | Azure Key Vault | Named, not built: [I-73](../../ISSUES.md#i-73-the-azure-key-vault-secret-provider-is-not-built) |

- A binding writes `secretRef: <reference>` among its settings (for example `content-configurations/banks/dib/core-banking.yaml`).
- Every host reads a secret through one interface, at the moment it needs it: `ISecretResolver` in `runtime/src/TransactHub.Security` for the runtime, `resolveSecret` in `studio/src/secrets` for the studio, its workers and its deploy runner.
- A value is never written to the branch, the store, a package, a log line or an exception. A secret's value prints as `[secret]`. A reference that cannot be resolved fails the step and names the reference only. A literal where a reference belongs is refused, and the message gives only its length.
- Check L37 (R-06-23) finds a literal secret: a key that names a secret (password, passphrase, secret, token, apiKey, privateKey, clientSecret, credential, or a name ending in one) holding a value, a `secretRef` that is not a reference, or a private key anywhere. It runs in `spec/tools/validate.py`, `npm run studio -- check` (every file on the content branch) and the exporter, where a package that carries one is not promotable and is not signed.
- The studio reads its GitHub token (`TH_GITHUB_TOKEN`) and the assistant's key (`ANTHROPIC_API_KEY`) from the environment at each request. It never stores, shows, logs or audits them (`studio/README.md`).
- A connection string in a test comes from the test's own container, never from a shared server.

## Signing released packages

Every workflow package the studio releases is signed, and nothing unsigned is stored, activated or deployed (R-09-23 to R-09-26; `spec/design/09-export-bindings.md`, section 9.9).

| | |
| --- | --- |
| Format | `release.json`, a canonical JSON statement of its kind (`workflow-package`, later `distribution` and `configuration`), the release id, workflow, release, target, tenant, change set, key id, signing time and the SHA-256 of every other file; `release.sig`, ECDSA P-256 with SHA-256 over it, DER, Base64 |
| Signing key | The studio's, by reference only: `TH_SIGNING_KEY=file:/run/secrets/th-signing-key.pem` (or `env:`, `vault:`). PKCS#8 PEM on P-256. Without it the studio publishes no workflow release and serves no download |
| Trusted signers | `TH_TRUSTED_SIGNERS`, the trusted-signers JSON or a reference to it, read again at each verification: several keys by key id, `retiredAt` for a key that has rotated out, a `revoked` list |
| Where it is | The release folder on the content branch, with an annotated tag `release/<workflow>/<release>/<target>` on the publishing commit (R-09-30) |
| Checked by | The runtime before it stores a release (`POST /releases`, `POST /import-branch`, `POST /sources/install` from its package source) and again before it activates one, recording each verification as a `release.verified` fact on the release; the deploy runner before any worker starts |
| By hand | `openssl dgst -sha256 -verify signer.pem -signature <(base64 -d release.sig) release.json`, or `cosign verify-blob --key signer.pem --signature release.sig release.json` |

Rotating a key: make a new one (`npm run studio -- signing-key <dir>` writes a key and its trusted-signers entry), add its public key to every installation's trusted signers, switch `TH_SIGNING_KEY` to it, then set `retiredAt` on the old key. What the old key signed before then still verifies. A key that may have leaked goes on the `revoked` list: from then on nothing it signed is stored or activated, even if it was stored before.

The signing key is the bank's trust in the studio's output. Keep it in the secret store, readable only by the studio's process. A hardware or remote signer (a KMS, Vault transit) is [I-74](../../ISSUES.md#i-74-the-studios-signing-key-is-held-in-its-process).

## The runtime's callers

The runtime belongs to the bank and keeps no identities: it takes the bank's identity provider's bearer JWTs only, and maps the bank's claims to its roles by the bank's role map (R-09-27, R-09-29). See [access-control.md](access-control.md). Its only trust in the studio is the signing keys above.

## Scans in CI

`.github/workflows/security.yml` runs on every push and pull request (R-09-28):

- a secret scan of the whole history and of the tree with gitleaks, pinned to a version and checked against its release checksum;
- check L37 with the spec's other checks;
- `npm audit --omit=dev --audit-level=high` on the studio's runtime dependencies;
- `dotnet list package --vulnerable --include-transitive` on the runtime, failing on any finding.

## Encryption: the rules that still hold

The source wrapped file encryption in one service with named profiles. Its rules are sound and carry over to whatever I-67 decides for decryption:

| Do | Do not |
| --- | --- |
| Decrypt and encrypt through one service, by a named profile | Call a crypto tool or library directly from an activity |
| Let configuration choose the provider and the key | Take a provider or key name from a request or from payment data |
| Fail the step when the provider fails | Fall back to another provider, a default, or the first one registered |
| Decide "is it encrypted?" from the content | Decide from the file name or extension. Cross-check the two, and reject a file whose name says encrypted when its content is not |
| Treat damaged ciphertext as a rejected file | Catch the error and process the bytes as plain text |
| Keep keys in mounted key rings and passphrases in the installation's secret store, by reference | Commit a key, a passphrase or a real encrypted sample |
| Give each scheme's, and each bank's, decryption keys their own key ring | Share one key ring across schemes or banks |
| Log only that a step ran and its outcome | Log plain text, ciphertext, keys, passphrases or key ids |
| Treat Base64 as a transport encoding | Call Base64 encryption |

## What is built

- The secret interface and its env:, file: and Vault providers, in the runtime and the studio. The Vault provider is tested against a real Vault dev server in a container (`VaultTests`).
- The core-banking gateway adapter reads its credential through the interface; a binding whose secret cannot be read fails the call before anything is sent.
- Package signing in the studio, verification in the runtime and the deploy runner, with rotation and revocation.
- Check L37 and the CI scans.
- `CbxFileNameRules` in `runtime/src/TransactHub.Activities.Wps` tells an encrypted salary file name from a plain one, exactly as the source does (golden corpus). It reads no content and decrypts nothing.
- The studio's sandboxes give a script no file system, network, clock or environment (`spec/CLAUDE.md`, Scripts-behind). The interface offers no way to a key; the sandbox is not a security boundary against hostile code (D14).

## Not built

- Decryption: the factory has no decrypt step, and the runtime decrypts nothing. Whether decryption becomes a factory activity behind `p.port.decryption.v1` is I-67, point 2.
- The Azure Key Vault provider (I-73), a remote signer (I-74), and erasure from an insert-only store (I-67, point 4).
