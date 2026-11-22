# Encryption and secrets

How files are decrypted and secrets are kept. It replaces the source's encryption guidelines and agent, in substance.

**Status.** Where decryption runs and how a secret is named in a binding are not decided: [I-67](../../ISSUES.md#i-67-secrets-keys-and-file-encryption-at-deployment). The runtime has no decryption and reads no secret today. What follows is what holds from the source, and what this repository already does.

## Secrets

- No secret is committed: not in code, configuration, a bank's folder, a sample, a test or a commit message. This repository is public. A bank's folder names a secret by reference; the value is set at deployment (`content-configurations/README.md`).
- The studio reads its GitHub token (`TH_GITHUB_TOKEN`) and the assistant's key (`ANTHROPIC_API_KEY`) from the environment at each request. It never stores, shows, logs or audits them (`studio/README.md`).
- Temporal's SQL Server configuration reads its address and password from the environment (`runtime/temporal-mssql/config/development-sqlserver.yaml`).
- A connection string in a test comes from the test's own container, never from a shared server.

## Encryption: the rules that still hold

The source wrapped file encryption in one service with named profiles. Its rules are sound and carry over to whatever I-67 decides:

| Do | Do not |
| --- | --- |
| Decrypt and encrypt through one service, by a named profile | Call a crypto tool or library directly from an activity |
| Let configuration choose the provider and the key | Take a provider or key name from a request or from payment data |
| Fail the step when the provider fails | Fall back to another provider, a default, or the first one registered |
| Decide "is it encrypted?" from the content | Decide from the file name or extension. Cross-check the two, and reject a file whose name says encrypted when its content is not |
| Treat damaged ciphertext as a rejected file | Catch the error and process the bytes as plain text |
| Keep keys in mounted key rings and passphrases in the installation's secret store | Commit a key, a passphrase or a real encrypted sample |
| Give each scheme's, and each bank's, decryption keys their own key ring | Share one key ring across schemes or banks |
| Log only that a step ran and its outcome | Log plain text, ciphertext, keys, passphrases or key ids |
| Treat Base64 as a transport encoding | Call Base64 encryption |

## What is built

- `CbxFileNameRules` in `runtime/src/TransactHub.Activities.Wps` tells an encrypted salary file name from a plain one, exactly as the source does (golden corpus). It reads no content and decrypts nothing.
- The studio's sandboxes give a script no file system, network, clock or environment (`spec/CLAUDE.md`, Scripts-behind). The interface offers no way to a key; the sandbox is not a security boundary against hostile code (D14).

## In the studio

The factory has no decrypt step, and the studio holds no key. Whether decryption becomes a factory activity behind a port is part of I-67.
