# Encryption and secrets

How secrets are named and read, how released packages are signed, and how files are decrypted. It replaces the source's encryption guidelines and agent, in substance.

**Status.** Secrets by reference, package signing and the CI scans were decided by the owner on 2026-11-22 and are built ([I-67](../../ISSUES.md#i-67-secrets-keys-and-file-encryption-at-deployment)). On the same day the owner decided to read Azure Key Vault and AWS Secrets Manager and Parameter Store directly ([I-73](../../ISSUES.md#i-73-the-azure-key-vault-secret-provider-is-not-built), done), and that the studio's signing key may stay in its process ([I-74](../../ISSUES.md#i-74-the-studios-signing-key-is-held-in-its-process), decided). Where decryption runs is still open (I-67, point 2).

## Secrets

- No secret is committed: not in code, configuration, a bank's folder, a sample, a test or a commit message. This repository is public. Test keys and tokens are made by the test that uses them and removed when it ends.
- A secret is named by reference, never by value (R-06-22):

| Reference | Where the value is | For |
| --- | --- | --- |
| `env:NAME` | A variable the deployment sets | Simple installations, CI |
| `file:/absolute/path` | A mounted secret file: a Kubernetes or Docker secret, a Vault agent's sink. `TH_SECRET_FILE_ROOTS` limits the folders it may read | Containers |
| `vault:mount/path#field` | A field of a HashiCorp Vault KV version 2 secret. `TH_VAULT_ADDR` names the Vault; its token is itself a reference in `TH_VAULT_TOKEN` (env: or file:); `TH_VAULT_NAMESPACE` if needed | Installations with Vault |
| `akv:<vault-name>/<secret-name>[#version]` | An Azure Key Vault secret, the latest version unless one is named (32 hex digits), at `https://<vault-name>.vault.azure.net/` (`TH_AKV_DNS_SUFFIX` for a sovereign cloud) | Installations on Azure |
| `aws-sm:<region>/<secret-id>[#json-field]` | An AWS Secrets Manager secret string, by name or ARN, or one field of it when it is a JSON object (`aws-sm:eu-west-1/th/core-banking#password`) | Installations on AWS |
| `aws-ssm:<region>/<parameter-name>` | An AWS Systems Manager Parameter Store SecureString, decrypted. A name with a '/' in it is a path from the root: `aws-ssm:eu-west-1/th/prod/token` reads `/th/prod/token`. A String parameter is refused | Installations on AWS |

- The cloud stores are read through their official SDKs, pinned (R-06-25):

| Store | Runtime (.NET) | Studio (Node) | Signs in with |
| --- | --- | --- | --- |
| Azure Key Vault | Azure.Security.KeyVault.Secrets 4.12.0, Azure.Identity 1.21.0 | @azure/keyvault-secrets 4.12.0, @azure/identity 4.14.1 | The first configured, in DefaultAzureCredential's order: client credentials (`AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, and `TH_AKV_CLIENT_SECRET`, a reference to env: or file:, never the secret itself), workload identity (`AZURE_FEDERATED_TOKEN_FILE`, as on AKS), managed identity (user-assigned by `AZURE_CLIENT_ID`, else system-assigned). `AZURE_AUTHORITY_HOST` for another cloud. A literal `AZURE_CLIENT_SECRET` is refused |
| AWS Secrets Manager, Parameter Store | AWSSDK.SecretsManager 4.0.100.23, AWSSDK.SimpleSystemsManagement 4.0.104.9 | @aws-sdk/client-secrets-manager 3.1162.0, @aws-sdk/client-ssm 3.1162.0 | The AWS default credential chain: environment, web identity (IRSA on EKS), the container (ECS) or instance role |

  Give the identity read access to the secrets it needs and nothing else: `Key Vault Secrets User` on the vault or the secret; `secretsmanager:GetSecretValue` on the secret's ARN; `ssm:GetParameter` on the parameter and `kms:Decrypt` on its key.
- A value read over the network (`vault:`, `akv:`, `aws-sm:`, `aws-ssm:`) is kept in the process for `TH_SECRET_CACHE_SECONDS` (default 300; 0 keeps nothing) and never used past it, nor when its store fails, so a revoked secret stops working within that time (R-06-26). When the system a credential opens refuses it, `WithSecret` (runtime) or `SecretStores.withSecret` (studio) reads it again from its store, once. Adapters do not call it yet ([I-106](../../ISSUES.md#i-106-a-rotated-secret-reaches-a-running-host-only-after-the-cache-time-or-a-restart)). env: and file: are read at each use.
- A binding writes `secretRef: <reference>` among its settings (for example `content-configurations/banks/dib/core-banking.yaml`).
- Every host reads a secret through one interface, at the moment it needs it: `ISecretResolver` in `runtime/src/TransactHub.Security` for the runtime, `resolveSecret` in `studio/src/secrets` for the studio, its workers and its deploy runner.
- A value is never written to the branch, the store, a package, a log line or an exception. A secret's value prints as `[secret]`. A reference that cannot be resolved fails the step and names the reference and the store's status or error code only (`Secret akv:th-vault/signing-key does not exist: Key Vault answered 404 SecretNotFound`), never the store's own text. A literal where a reference belongs is refused, and the message gives only its length.
- Check L37 (R-06-23) finds a literal secret: a key that names a secret (password, passphrase, secret, token, apiKey, privateKey, clientSecret, credential, or a name ending in one) holding a value, a `secretRef` that is not a reference, or a private key anywhere. It runs in `spec/tools/validate.py`, `npm run studio -- check` (every file on the content branch) and the exporter, where a package that carries one is not promotable and is not signed.
- The studio reads its GitHub token (`TH_GITHUB_TOKEN`) and the assistant's key (`ANTHROPIC_API_KEY`) from the environment at each request. It never stores, shows, logs or audits them (`studio/README.md`).
- A connection string in a test comes from the test's own container, never from a shared server.

## Signing released packages

Every workflow package the studio releases is signed, and nothing unsigned is stored, activated or deployed (R-09-23 to R-09-26; `spec/design/09-export-bindings.md`, section 9.9).

| | |
| --- | --- |
| Format | `release.json`, a canonical JSON statement of its kind (`workflow-package`, later `distribution` and `configuration`), the release id, workflow, release, target, tenant, change set, key id, signing time and the SHA-256 of every other file; `release.sig`, ECDSA P-256 with SHA-256 over it, DER, Base64 |
| Signing key | The studio's, by reference only: `TH_SIGNING_KEY=file:/run/secrets/th-signing-key.pem` (or `env:`, `vault:`, `akv:`, `aws-sm:`, `aws-ssm:`). PKCS#8 PEM on P-256. Without it the studio publishes no workflow release and serves no download |
| Trusted signers | `TH_TRUSTED_SIGNERS`, the trusted-signers JSON or a reference to it, read again at each verification: several keys by key id, `retiredAt` for a key that has rotated out, a `revoked` list |
| Where it is | The release folder on the content branch, with an annotated tag `release/<workflow>/<release>/<target>` on the publishing commit (R-09-30) |
| Checked by | The runtime before it stores a release (`POST /releases`, `POST /import-branch`, `POST /sources/install` from its package source) and again before it activates one, recording each verification as a `release.verified` fact on the release; the deploy runner before any worker starts |
| By hand | `openssl dgst -sha256 -verify signer.pem -signature <(base64 -d release.sig) release.json`, or `cosign verify-blob --key signer.pem --signature release.sig release.json` |

Rotating a key: make a new one (`npm run studio -- signing-key <dir>` writes a key and its trusted-signers entry), add its public key to every installation's trusted signers, switch `TH_SIGNING_KEY` to it, then set `retiredAt` on the old key. What the old key signed before then still verifies. A key that may have leaked goes on the `revoked` list: from then on nothing it signed is stored or activated, even if it was stored before.

The signing key is the bank's trust in the studio's output. Keep it in the secret store, readable only by the studio's identity. The owner decided on 2026-11-22 that the key may be held in the studio's process: no HSM or remote signer is required ([I-74](../../ISSUES.md#i-74-the-studios-signing-key-is-held-in-its-process)). It is still read only through the secret interface, so it may live in any of the stores above (`TH_SIGNING_KEY=akv:th-studio/signing-key`, `aws-sm:eu-west-1/th/studio-signing-key`). A key in a store over the network is read when the studio starts and held until it restarts, so restart the studio when you switch `TH_SIGNING_KEY` or rotate the key in place ([I-106](../../ISSUES.md#i-106-a-rotated-secret-reaches-a-running-host-only-after-the-cache-time-or-a-restart)).

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

- The secret interface and its env:, file:, Vault, Azure Key Vault, AWS Secrets Manager and Parameter Store providers, in the runtime and the studio, with the short cache and the read-again on refusal. The Vault provider is tested against a real Vault dev server in a container (`VaultTests`); the AWS providers against LocalStack 4.14.0 in a container named for the run (`AwsSecretsTests`, `studio/test/secrets-cloud.test.ts`); the Azure provider, on the official SDK, against an in-process fake of the Key Vault REST protocol and the token endpoint, given to the SDK as its transport (`FakeKeyVault`, `studio/test/support/fake-key-vault.ts`), since there is no official emulator. What these do not prove is [I-105](../../ISSUES.md#i-105-the-cloud-secret-providers-are-proved-against-a-fake-and-localstack-not-the-real-services).
- The core-banking gateway adapter reads its credential through the interface; a binding whose secret cannot be read fails the call before anything is sent.
- Package signing in the studio, verification in the runtime and the deploy runner, with rotation and revocation.
- Check L37 and the CI scans.
- `CbxFileNameRules` in `runtime/src/TransactHub.Activities.Wps` tells an encrypted salary file name from a plain one, exactly as the source does (golden corpus). It reads no content and decrypts nothing.
- The studio's sandboxes give a script no file system, network, clock or environment (`spec/CLAUDE.md`, Scripts-behind). The interface offers no way to a key; the sandbox is not a security boundary against hostile code (D14).

## Not built

- Decryption: the factory has no decrypt step, and the runtime decrypts nothing. Whether decryption becomes a factory activity behind `p.port.decryption.v1` is I-67, point 2.
- Erasure from an insert-only store (I-67, point 4).
- Adapters reading a refused credential again (I-106), and a check of the cloud providers against a real vault or account (I-105).
