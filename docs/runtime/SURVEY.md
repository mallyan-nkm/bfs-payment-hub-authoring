# Survey of the TransactHub source (commit 666f1ee), read only

Paths are relative to the root of `TransactHub-src.zip`.

## 1. Data model and databases

### Databases and migration contexts
- Two SQL Server databases, `PaymentHub` and `PaymentHub_Report`, restored by `restore-databases.sql` (SQL Server 2025).
- 15 EF migration contexts in `services/platform/Database/PaymentHub.Database/Contexts/`:

| Context | Schema | Tables | Kind |
|---|---|---|---|
| PlatformSchemaDbContext | platform | 121 | generic, with scheme-flavoured tables (Corporate_*, FtsInwardDuplicateCache, SchemeParticipants) |
| WpsSchemaDbContext | wps | 64 | WPS |
| Fts/Npss/Ipi/Cbft/Edda SchemaDbContext | one each | 2 each | the same `transaction_headers` and `cycle_states`, copied per scheme |
| XbdrSchemaDbContext | xbdr | 1 | SWIFT (`Transactions`: UETR, BICs, gpi) |
| AdibSchemaDbContext | adib | 1 | a bank (`FftsMessages`) |
| ExceptionSchemaDbContext | exception | 4 | generic |
| Report contexts (platform, wps, smartintegration) | Report DB | 6, 21, 4 | report copies |

- About 30 service DbContexts map into these schemas. Three of them are WPS-specific: `WpsBusinessDbContext`, `WpsUploadHistoryDbContext` and `WpsSifReportDbContext`.
- `services/shared/dotnet/PaymentHub.Shared/Infrastructure/Persistence/RailSchemaResolver.cs` routes by scheme to a schema and falls back to `ipi`.

### ISO 20022
- There are no pacs, pain or camt types and no XSDs. ISO 20022 appears only as message-type strings and in the MX↔MT converter templates.
- The nearest thing to pacs.008 is the flat `transaction_headers` table (`Models/Fts/transaction_headers.cs`). It has no structured address, agent identifiers, UETR, remittance information or charge bearer.

### Workflow context
- `WorkflowInput.Variables` and `InitialContext` are `Dictionary<string,string>` (`services/shared/dotnet/PaymentHub.Engine.Abstractions/WorkflowInput.cs`).
- `DslInterpreter._state` is a string dictionary. The whole state goes to each activity, and every output is merged back.
- Personal data in context:
  - `services/financial/WpsBusiness/PaymentHub.WpsBusiness.API/Endpoints/UploadSifFileEndpoints.cs:482–621` puts cif, email, account, IBAN, corporate and MOL ids into the context.
  - `DslWorkflowRunner.cs:1086` passes whole batch records as variables.

### Updates in place vs insert-only
- **Mutable:**
  - `{scheme}.usp_UpsertCycleStates` (MERGE … UPDATE);
  - the hot-cache upserts;
  - raw UPDATEs of the FTS inward tables;
  - EF status writes on SettlementEntries, PaymentQueueState, WorkflowInstances, batch and SFTP queues.
- **Insert-only (keep):**
  - `RailTransactionRepository` (`TransactionStateLog`);
  - `workflow_events`;
  - `CbPostingRepository`;
  - the timeline and status-history tables;
  - the audit hash chain (`services/platform/Audit/PaymentHub.Audit.Application/Services/HashChainService.cs`).

### SQL Server lock-in
- `new SqlConnection` in 102 files.
- Table-valued parameters in 18 files, with about 30 TVP types.
- MERGE with HOLDLOCK, 24 stored procedures, partition maintenance and memory-optimised filegroups.
- `OUTPUT INSERTED` in 37 files, lock hints in 36, `SYSUTCDATETIME`/`GETUTCDATE` in 114.
- The report sync reads change data capture (`services/platform/ReportSync/PaymentHub.ReportSync.Worker/Cdc/CdcChangeReader.cs`).

## 2. Engines and deployment

### The seam and the adaptors
- **The seam:** `services/shared/dotnet/PaymentHub.Engine.Abstractions/`, with `IWorkflowRuntime`, `IWorkflowEngineClient`, `IWorkflowScheduler`, `IActivityHost`, `IEngineHistoryReader` and `IEngineCapabilities`.
- **Engine selection:** by `WorkflowEngine:Provider`, with `Temporal` and `Dtfx` only.
- **Temporal:** `services/core/engines/PaymentHub.Engine.Temporal/`. `Hosting/PaymentDslWorkflow.cs` forwards 15 signals.
- **DTFx:** `services/core/engines/PaymentHub.Engine.Dtfx/`, on `Microsoft.DurableTask.SqlServer`, schema `dt`.
  - `IsPatched` always returns true.
  - There is no task-queue routing.
  - Retries are hand-rolled with timers.
  - Schedules are emulated by `DtfxScheduleTicker`.
- There is no Elsa and no Flowable. Elsa is marked out of scope in `specs/001-workflow-engine-seam-and-dtfx/spec.md`.

### The interpreter
- `services/core/WorkflowEngine/PaymentHub.WorkflowEngine.Application/Workflows/DslWorkflowRunner.cs` (2,079 lines) and `DslInterpreter.cs`.
- **Queues:** `ResolveTaskQueue` takes the explicit `task_queue`, then `queue_scope`, then `activity_tier`.
- **Compensation:** a stack, run in reverse.
- **Waits:** timers exist only as signal timeouts.
- **Child workflows:** only through fan-out.
- **Patch versions:** a 14-level `IsPatched` chain (lines 197–221).
- **Hard-coded step ids:** lines 538–566.
- **Coded workflows:** `services/core/workers/PaymentHub.Worker.Orchestrator/Workflows/*` are written in C#, not the DSL.

### The DSL
- `services/shared/dotnet/PaymentHub.Shared/Domain/Dsl/PaymentDslDefinition.cs`.
- Step types: sequential, conditional, parallel, fan_out, hook and wait_for_signal.
- `state_effect` is ignored.
- Conditions are `==`, `!=` or a truthy check.
- There is no published JSON Schema.

### Versioning
- **Definitions:** stored in `platform.WorkflowDefinitions` (`WorkflowRegistration.cs`, `WorkflowRepository.cs`).
  - Lifecycle: Draft → Review → Approved → Production → Deprecated, with four eyes.
  - The DSL text can still be changed until Production, and when deprecated.
- **Pinning a run:** a run loads its definition once through `LoadDslDefinition` (`services/core/engines/PaymentHub.Engine.Hosting/DslDefinitionActivities.cs`), which pins it.
- **Activity code is not pinned:**
  - `DynamicActivityRegistry` is keyed by name only.
  - Hot reload goes over MQTT (`ActivityHotReloadCoordinator.cs`).
  - The `_assembly_version` written into step JSON is never read.
  - There is no Temporal Build-ID versioning.
- **Rules:** NRules (`services/platform/RuleEngine/`), with `RuleVersion` insert-only and effective-dated.
  - `RuleRepository.cs:161` picks the newest version before filtering by date.
  - `RuleService.cs:353` uses `DateTime.UtcNow` as the as-of date.
- `release-ledger.json` is `{}`.

### Temporal persistence
- `infra/docker/docker-compose.infra.yml:242` runs `temporalio/auto-setup:1.27` on PostgreSQL 16.
- SQL Server is used only by DTFx (`docker-compose.engine-lite.yml`) and by the application databases.

### Conformance tests
- `tests/PaymentHub.EngineConformance.Tests`: cases C01–C18 on in-memory, Temporal (time-skipping) and DTFx (an external SQL Server, `PAYMENTHUB_CONFORMANCE_DTFX_CONNECTION_STRING`).
- Replay tests are in `tests/PaymentHub.WorkflowTests/Wps/PaymentDslWorkflowReplayTests.cs`.

## 3. WPS and the banks

### WPS activities
- `services/activities/platform/PaymentHub.Activities.Platform.Wps` registers about 112 names (about 95 behaviours) in 52 files.

| Group | Examples | Target |
|---|---|---|
| Intake, audit queue, read, precheck | `InitializeSifUploadRequestProcessing`, `PrecheckSifUpload` | p.* |
| Decrypt | `wps.DecryptSif` | p.* |
| File name, date, MOL corporate | `ValidateSifFilenameStructureAndFormat`, `ResolveAndVerifyCorporateRegistrationByMolEstablishmentId` | uae.wps.* |
| SIF parse and validate | `ParseSif`, `ValidateSif`, `BuildValidationResponse` | uae.wps.* |
| Duplicates | `CheckDuplicateSif`, `CheckForDuplicatePosting…` | p.* pattern, uae.wps.* keys |
| Approval, payment queue | `EvaluateApprovalPolicy`, `EnterPaymentQueue` | p.* |
| Charges | `wps.CalculateCharges`, `ConstructChargePostingTransactions` | p.*; slabs, VAT and narration are b.* values |
| Balance, posting, reverse, status, reconcile | `BalanceEnquiry`, `CorePosting`, `ReverseCorePosting`, `ReconcilePosting` | p.* through a core-banking port |
| SIF generation, CB SFTP, ACK/NAK | `GenerateCbxSif`, `SubmitSifToCb`, `ProcessCbResponse` | uae.wps.* (SFTP is p.*) |
| Inward PIF/PSI, PRC, DIF, RFA | `ParseAndSaveInwardPif`, `GenerateInwardPrcFile`, `ApplyDifNak`, `PostRfaReversal` | uae.wps.* |
| RRI | `SelectRriBlockCandidates`, `BlockInwardRriLines`, `ReleaseInwardRriLines` | uae.wps.*; block and release calls are b.* ports |
| DE bulk | `GenerateDeBulkFile`, `SubmitDeBulkUpload`, `RepostFailedDeUploads` | uae.wps.*; the upload is a b.* port |
| Posting windows, restricted banks | `EvaluateCbCallWindow`, `AwaitCbPostingWindow`, `Rb*` | p.* scheduler; times are b.* values |
| Exchange rate | `RefreshExchangeRates` | p.* |

### Bank forks
- **DIB** (`services/activities/bank/Dib/PaymentHub.Activities.Bank.Dib.Wps`, about 4,600 lines): 17 activities, with copies of the repository, transaction codes and posting legs.
- **ADIB:** 14 activities. RTC exists only here.
- **ENBD and FAB:** 8 identical HTTP stubs each.

### Hard-coded rules
- `SifUploadRules.cs`: path, file name and date, MOL/CIF/account/email match, UTC+4.
- `CbxFileNameRules.cs`: rules 1–5.
- `CbxRecordRules.cs`: EDR 4/5/14/32/36/77, EVP 46/47/54–60, rules 7 and 79.
- `CbxScrRules.cs`: rules 66, 81, 82 and 83.
- `CbxRuleSettings.cs` already reads its parameters from `wps.ParamConfiguration`.
- The SSM checks inline in `WpsPlatformActivities.cs` (~820–1000).
- The DE, DIF, RFA and PRC rules, the stuck and await rules, and the RRI rule set.
- The same validation is copied into four banks.

### Bank settings, with targets
- **Core-banking configuration** in `DibWpsActivities.cs` ~1950–2210 (mode, variant, ServiceCode, ExternalHostId, TxnFlag, UploadBranch, AuthStatus, paths): port binding.
- **GL accounts and narration** (`DibWpsActivities.Independent.cs:199–204`): bank values.
- **VAT_Percentage** (default 5): a bank value.
- **Transaction codes:** uae.wps codes, with a bank override.
- **`{tenant}RoutingCode`:** the neutral `BankRoutingCode`.
- **CBX parameters seeded for DIB:** bank values with UAE defaults.
- **Operation profiles** `CoreBanking:Operations:*`: port bindings.
- **`ExchangeRateReplyReader.cs`:** an adapter.
- **Queue pins** `wps-dib-*`: tenant routing.
- **Default tenant "DIB"** in `CbBulkPostingOptions`, `ExchangeRateRefreshOptions`, `RriBlockScheduleOptions` and `RriReleaseScheduleOptions`: required, with no default.
- **The simulator** calls `GetParamAsync("DIB", …)`.

### Bank names in shared code
- `DslWorkflowRunner.cs:1298`.
- `RriBlockNotEligibleReasons.NotDibAccount`.
- The `EventSource "DibWpsBankWorker"`.
- The legacy state keys `dibChargeAmount` and `dibChargeAccount`.
- Default tenants in `RoutingModule.cs:1218`, the ApiGateway `Program.cs` and `FtsFileWatcherService.cs:187`.
- Workflow names `WF_DIB_WPS_*` and `WF_FAB_WPS_*`.
- The routing-rule seeds `WPS-DIB-OUTWARD-*`.
- The `adib` schema.

### Tests
- **Unit tests:** about 800 WPS tests in `tests/PaymentHub.UnitTests`, and 240 in `PaymentHub.UnitTests.WpsReports`.
- **Integration tests:** 469 in `tests/PaymentHub.IntegrationTests/Wps`, and about 1,469 in the inward folders (DE 187, file watcher 419, PIF 200, PRC 119, PSI 88, RRI 435).
- There are no tests for ADIB, ENBD or FAB WPS, and none for RTC.
