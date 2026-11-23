# WPS parity matrix

Generated from the TransactHub source by `docs/runtime/parity.py`. Every activity the source registers for WPS, on the platform and in each bank, with where it goes in the north-star layout and what proves it today. "Golden" means the runtime's port answers a corpus generated from the source's own code exactly (`runtime/tests/TransactHub.Store.Tests/Golden`).

Platform WPS activities: 101. Bank WPS activities: 46 (every one is removed: a bank is configuration, hooks and port bindings).

## Rule classes

| Source | Runtime | Proof |
| --- | --- | --- |
| `CbxFileNameRules` | `TransactHub.Activities.Wps` | golden (1,977 cases in all) |
| `CbxRecordRules` | `TransactHub.Activities.Wps` | golden (1,977 cases in all) |
| `CbxScrRules` | `TransactHub.Activities.Wps` | golden (1,977 cases in all) |
| `CbxRuleSettings` | `TransactHub.Activities.Wps` | golden (1,977 cases in all) |
| `SifUploadRules (file name, date, corporate match)` | `TransactHub.Activities.Wps` | golden (1,977 cases in all) |
| `CbxSifWriter` | `TransactHub.Activities.Wps` | golden (1,977 cases in all) |
| `WpsAckNakFile (ACK/NAK name, parse, build)` | `TransactHub.Activities.Wps` | golden (1,977 cases in all) |
| `WpsEmployeeBankClassifier` | `TransactHub.Activities.Wps` | golden (1,977 cases in all) |
| `WpsRoutingCode` | `TransactHub.Activities.Wps` | golden (1,977 cases in all) |
| `AccountMasking` | `TransactHub.Activities.Wps` | golden (1,977 cases in all) |
| `InwardFileText` | `TransactHub.Activities.Wps.Inward` | the source's unit tests (446 cases, `runtime/tests/TransactHub.Activities.Wps.Tests/Source`) |
| `InwardPifParser` | `TransactHub.Activities.Wps.Inward` | the source's unit tests (446 cases, `runtime/tests/TransactHub.Activities.Wps.Tests/Source`) |
| `InwardPsiParser` | `TransactHub.Activities.Wps.Inward` | the source's unit tests (446 cases, `runtime/tests/TransactHub.Activities.Wps.Tests/Source`) |
| `PrcFileBuilder, PrcFileNamePolicy, PrcNakFileReader` | `TransactHub.Activities.Wps.Inward` | the source's unit tests (446 cases, `runtime/tests/TransactHub.Activities.Wps.Tests/Source`) |
| `DifLines, DifFileBuilder, DifAmountRule, DifNakLineResolver` | `TransactHub.Activities.Wps.Inward` | the source's unit tests (446 cases, `runtime/tests/TransactHub.Activities.Wps.Tests/Source`) |
| `RfaLines, RfaFileBuilder, RfaFileNamePolicy, RfaAmountRule` | `TransactHub.Activities.Wps.Inward` | the source's unit tests (446 cases, `runtime/tests/TransactHub.Activities.Wps.Tests/Source`) |
| `InwardRriParser` | `TransactHub.Activities.Wps.Inward` | the source's unit tests (446 cases, `runtime/tests/TransactHub.Activities.Wps.Tests/Source`) |

Not yet ported (P2): the SSM structure checks inline in `WpsPlatformActivities.cs`, `AccTxnDecisionRules`, `RbPostingClassifier`. The DE file layout is in the `file-bulk` adapter (R-12-15).

## Platform activities

| Activity | Source file | Target layer | Target | Status |
| --- | --- | --- | --- | --- |
| `EvaluateExchangeRateStaleness` | `ExchangeRateRefreshActivities.cs` | p | p exchange-rate refresh (not WPS); rates are a dial of p.enrich.fx-rate.v1 | no feed: I-189 |
| `GenerateSifFile` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.map.sif-out (CentralBankSif from the ISO 20022 store) | ported, golden; generated from the store |
| `ParseRtcRecords` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.wf.return-in.v1 with uae.wps.map.rtc-in.v2 (payment-level creditor account; the source parses no RTC lines) | on the runtime: map native, posting through the binding (WPS-RTC-001, NativeInwardTests) |
| `ParseSifRecords` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.map.sif-in + uae.wps.validate.salary-file / salary-record | rules ported, golden; activity on the studio contract: P2 |
| `RecordExchangeRateRunOutcome` | `ExchangeRateRefreshActivities.cs` | p | p exchange-rate refresh (not WPS); rates are a dial of p.enrich.fx-rate.v1 | no feed: I-189 |
| `RefreshExchangeRates` | `ExchangeRateRefreshActivities.cs` | p | p exchange-rate refresh (not WPS); rates are a dial of p.enrich.fx-rate.v1 | no feed: I-189 |
| `RouteToNonWpsHandler` | `WpsPlatformActivities.cs` | p | front door workflow-select | studio front door runs on Temporal |
| `RouteWpsPayment` | `WpsPlatformActivities.cs` | p | front door workflow-select | studio front door runs on Temporal |
| `ValidateWpsSifFile` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.map.sif-in + uae.wps.validate.salary-file / salary-record | rules ported, golden; activity on the studio contract: P2 |
| `Wps.Hook.PostCorePosting` | `WpsHookActivities.cs` | p via core-banking port; b binding | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking | built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116 |
| `Wps.Hook.PostSettlement` | `WpsHookActivities.cs` | p hook points | workflow hook points with bank bindings | hooks run on Temporal (kit CK-HOOK-01..04) |
| `Wps.Hook.PostValidation` | `WpsHookActivities.cs` | p hook points | workflow hook points with bank bindings | hooks run on Temporal (kit CK-HOOK-01..04) |
| `Wps.Hook.PreCorePosting` | `WpsHookActivities.cs` | p via core-banking port; b binding | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking | built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116 |
| `Wps.Hook.PreFtsDispatch` | `WpsHookActivities.cs` | p hook points | workflow hook points with bank bindings | hooks run on Temporal (kit CK-HOOK-01..04) |
| `Wps.Hook.PreReturnResult` | `WpsHookActivities.cs` | p hook points | workflow hook points with bank bindings | hooks run on Temporal (kit CK-HOOK-01..04) |
| `wps.ApplyDifAck` | `WpsPlatformActivities.DifAck.cs` | uae.wps | uae.wps.map.ack-in and the leg's NAK path | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| `wps.ApplyDifNak` | `WpsPlatformActivities.DifNak.cs` | uae.wps | uae.wps.map.ack-in and the leg's NAK path | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| `wps.ApplyPrcAck` | `WpsPlatformActivities.PrcAck.cs` | uae.wps | uae.wps.map.ack-in and the leg's NAK path | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| `wps.ApplyPrcNak` | `WpsPlatformActivities.PrcNak.cs` | uae.wps | uae.wps.map.ack-in and the leg's NAK path | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| `wps.ApplyRfaAck` | `WpsPlatformActivities.RfaAck.cs` | uae.wps | uae.wps.map.ack-in and the leg's NAK path | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| `wps.ApplyRfaNak` | `WpsPlatformActivities.RfaNak.cs` | uae.wps | uae.wps.map.ack-in and the leg's NAK path | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| `wps.AwaitCbPostingWindow` | `WpsPlatformActivities.CbPostingAwait.cs` | p; b values | p.decide.await-posting-window.v1 (schedule-gated await; native: PostingWindow) | gate ported; await in the inward legs on all engines |
| `wps.AwaitDifResponse` | `WpsPlatformActivities.InwardDifAwait.cs` | uae.wps | uae.wps.map.dif-out.v2 (native: DifLines, DifFileBuilder); ACK/NAK uae.wps.map.ack-in.v1 | ported, source unit tests; leg on all engines |
| `wps.AwaitPrcResponse` | `WpsPlatformActivities.InwardPrcAwait.cs` | uae.wps | uae.wps.map.prc-out.v2 (native: PrcFileBuilder, PrcFileNamePolicy); ACK/NAK uae.wps.map.ack-in.v1 | ported, source unit tests; leg on all engines |
| `wps.AwaitRestrictedPostingWindow` | `WpsPlatformActivities.RbAwait.cs` | p; b values | p.decide.await-posting-window.v1 (schedule-gated await; native: PostingWindow) | gate ported; await in the inward legs on all engines |
| `wps.AwaitRfaResponse` | `WpsPlatformActivities.InwardRfaAwait.cs` | uae.wps | uae.wps.map.rfa-out.v1 (native: RfaLines, RfaFileBuilder, RfaFileNamePolicy); ACK/NAK uae.wps.map.ack-in.v1 | ported, source unit tests; leg on all engines |
| `wps.BalanceEnquiry` | `WpsPlatformActivities.CoreBanking.cs` | p via core-banking port; b binding | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking | built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116 |
| `wps.BuildValidationResponse` | `WpsPlatformActivities.Names.cs` | p + uae.wps | the channel response (p.notify.channel) with the uae.wps error codes | error codes kept in the ported rules; response: P2 |
| `wps.CalculateCharges` | `WpsPlatformActivities.Charges.cs` | p + b values | p.enrich.charges.v1 on the bank's slabs, posted by p.post.accounting.v2 (R-12-23, R-12-25) | built, native code-behind (PlatformNativeTests); DIB slabs: I-58 |
| `wps.CheckDuplicateSif` | `WpsPlatformActivities.Names.cs` | p (pattern) + uae.wps (keys) | p.validate.duplicate with uae.wps keys | studio factory activity; source keys: P2 |
| `wps.CheckForDuplicateFilenameSubmission` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.validate.salary-file (CBX file-name rules 1–7) | rules ported, golden |
| `wps.CheckForDuplicatePostingAndPrepareCoreBankingRequest` | `WpsPlatformActivities.cs` | p (pattern) + uae.wps (keys) | p.validate.duplicate with uae.wps keys | studio factory activity; source keys: P2 |
| `wps.CheckForDuplicateSifFileSubmission` | `WpsPlatformActivities.cs` | p (pattern) + uae.wps (keys) | p.validate.duplicate with uae.wps keys | studio factory activity; source keys: P2 |
| `wps.CheckLostDeFiles` | `WpsPlatformActivities.LostFile.cs` | p core-banking adapter (bulk); b binding | the file-bulk adapter of p.port.core-banking.v1, not a WPS step (R-12-15) | built (FileBulkCoreBankingTests); one file per batch: I-132 |
| `wps.ConsolidateValidationErrorsAndGenerateResponse` | `WpsPlatformActivities.cs` | p + uae.wps | the channel response (p.notify.channel) with the uae.wps error codes | error codes kept in the ported rules; response: P2 |
| `wps.ConstructChargePostingTransactions` | `WpsPlatformActivities.cs` | p + b values | p.enrich.charges.v1 on the bank's slabs, posted by p.post.accounting.v2 (R-12-23, R-12-25) | built, native code-behind (PlatformNativeTests); DIB slabs: I-58 |
| `wps.ConstructSalaryAmountPostingTransaction` | `WpsPlatformActivities.cs` | p via core-banking port; b binding | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking | built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116 |
| `wps.CorePosting` | `WpsPlatformActivities.CoreBanking.cs` | p via core-banking port; b binding | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking | built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116 |
| `wps.DecryptSif` | `WpsPlatformActivities.Decryption.cs` | p | p.protect.decrypt-file.v1 in the File API on arrival (R-09-83) | built: openpgp and gnupg (EncryptionTests, FileApiTests) |
| `wps.DetermineDeOutcome` | `WpsPlatformActivities.DeOutcome.cs` | p core-banking adapter (bulk); b binding | the file-bulk adapter of p.port.core-banking.v1, not a WPS step (R-12-15) | built (FileBulkCoreBankingTests); one file per batch: I-132 |
| `wps.EvaluateCbCallWindow` | `WpsPlatformActivities.CbPostingWindow.cs` | p; b values | p.decide.await-posting-window.v1 (schedule-gated await; native: PostingWindow) | gate ported; await in the inward legs on all engines |
| `wps.ExtractAndParseSifUploadRequestJson` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.map.sif-in + uae.wps.validate.salary-file / salary-record | rules ported, golden; activity on the studio contract: P2 |
| `wps.FetchSalaryFilesReadyForCoreBankingPosting` | `WpsPlatformActivities.cs` | p via core-banking port; b binding | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking | built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116 |
| `wps.GenerateCbxSif` | `WpsPlatformActivities.SifGeneration.cs` | uae.wps | uae.wps.map.sif-out (CentralBankSif from the ISO 20022 store) | ported, golden; generated from the store |
| `wps.GenerateDeBulkFile` | `WpsPlatformActivities.DeBulkFile.cs` | p core-banking adapter (bulk); b binding | the file-bulk adapter of p.port.core-banking.v1, not a WPS step (R-12-15) | built (FileBulkCoreBankingTests); one file per batch: I-132 |
| `wps.GenerateInwardDifFile` | `WpsPlatformActivities.InwardDif.cs` | uae.wps | uae.wps.map.dif-out.v2 (native: DifLines, DifFileBuilder); ACK/NAK uae.wps.map.ack-in.v1 | ported, source unit tests; leg on all engines |
| `wps.GenerateInwardPrcFile` | `WpsPlatformActivities.InwardPrc.cs` | uae.wps | uae.wps.map.prc-out.v2 (native: PrcFileBuilder, PrcFileNamePolicy); ACK/NAK uae.wps.map.ack-in.v1 | ported, source unit tests; leg on all engines |
| `wps.InitializeAndValidateSifUploadRequest` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.map.sif-in + uae.wps.validate.salary-file / salary-record | rules ported, golden; activity on the studio contract: P2 |
| `wps.InitializePostingToNostroProcess` | `WpsPlatformActivities.cs` | p | posting preparation inside p.post.accounting, read from the store by id | studio factory activities run on Temporal; source parity: P2 |
| `wps.InitializeSifUploadRequestProcessing` | `WpsPlatformActivities.cs` | p (intake) + uae.wps (MOL) | File API intake and front door (R-09-82, R-09-85); the corporate register and the WPS match (R-12-27) | MatchCorporate ported, golden; intake built |
| `wps.InitiateSifFileParsingAndBusinessValidation` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.map.sif-in + uae.wps.validate.salary-file / salary-record | rules ported, golden; activity on the studio contract: P2 |
| `wps.LocateAndReadSalaryFileFromConfiguredLocalPath` | `WpsPlatformActivities.cs` | p (intake) + uae.wps (MOL) | File API intake and front door (R-09-82, R-09-85); the corporate register and the WPS match (R-12-27) | MatchCorporate ported, golden; intake built |
| `wps.NotifyChannelAccepted` | `WpsPlatformActivities.Names.cs` | p.notify + uae.wps | p.notify.channel; MOHRE notify as a bank hook | studio factory notify runs on Temporal; ministry status adapter built (R-12-16, I-118) |
| `wps.NotifyChannelRejected` | `WpsPlatformActivities.Names.cs` | p.notify + uae.wps | p.notify.channel; MOHRE notify as a bank hook | studio factory notify runs on Temporal; ministry status adapter built (R-12-16, I-118) |
| `wps.ParseAndQueueSifFile` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.map.sif-in + uae.wps.validate.salary-file / salary-record | rules ported, golden; activity on the studio contract: P2 |
| `wps.ParseAndSaveInwardPif` | `WpsPlatformActivities.InwardPif.cs` | uae.wps | uae.wps.map.pif-in.v1 / psi-in.v1 (native: InwardPifParser, InwardPsiParser); the agent leg | ported, source unit tests; leg on all engines; PSI map not wired (I-84) |
| `wps.ParseAndSaveInwardPsi` | `WpsPlatformActivities.InwardPsi.cs` | uae.wps | uae.wps.map.pif-in.v1 / psi-in.v1 (native: InwardPifParser, InwardPsiParser); the agent leg | ported, source unit tests; leg on all engines; PSI map not wired (I-84) |
| `wps.ParseJsonDocumentAndExtractSifUploadFields` | `WpsPlatformActivities.cs` | p (intake) | File API intake; the file kept in the store with its signed hash (R-09-82) | built: File API (FileApiTests) |
| `wps.ParseSif` | `WpsPlatformActivities.Names.cs` | uae.wps | uae.wps.map.sif-in + uae.wps.validate.salary-file / salary-record | rules ported, golden; activity on the studio contract: P2 |
| `wps.ParseSifControlRecordAndExtractMetadata` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.map.sif-in + uae.wps.validate.salary-file / salary-record | rules ported, golden; activity on the studio contract: P2 |
| `wps.PostRestrictedBankPayment` | `WpsPlatformActivities.RbPost.cs` | p; b values | p.decide.await-posting-window.v1 (schedule-gated await; native: PostingWindow) | gate ported; await in the inward legs on all engines |
| `wps.PostRfaReversal` | `WpsPlatformActivities.RfaReversal.cs` | uae.wps via core-banking port | uae.wps.post.return.v1 (suspense to settlement, the returns decision amount) | on the runtime (e2e-wps-inward-native) |
| `wps.ProcessCbResponse` | `WpsPlatformActivities.Names.cs` | uae.wps | uae.wps.map.ack-in and the leg's NAK path | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| `wps.ProcessCentralBankAcknowledgmentFilesAndUpdateSifStatus` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.map.ack-in and the leg's NAK path | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| `wps.ProcessSifAckResponseAndNotifyChannelOfAcceptance` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.map.ack-in and the leg's NAK path | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| `wps.ProcessSifNakResponseAndNotifyChannelOfRejection` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.map.ack-in and the leg's NAK path | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| `wps.QueryCorePostingStatus` | `WpsPlatformActivities.CoreBanking.cs` | p via core-banking port; b binding | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking | built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116 |
| `wps.QueueSif` | `WpsPlatformActivities.Names.cs` | p (intake) + uae.wps (MOL) | File API intake and front door (R-09-82, R-09-85); the corporate register and the WPS match (R-12-27) | MatchCorporate ported, golden; intake built |
| `wps.RaiseCbPostingStuck` | `WpsPlatformActivities.CbPostingDispatch.cs` | p via core-banking port; b binding | p.post.accounting.v1 through the binding, in the posting window | on the runtime (CoreBankingPostingTests, e2e-wps-core-banking) |
| `wps.RaiseDifNakItem` | `WpsPlatformActivities.DifNak.cs` | uae.wps | uae.wps.map.ack-in and the leg's NAK path | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| `wps.RaiseInwardPifException` | `WpsPlatformActivities.InwardPifRaise.cs` | uae.wps | uae.wps.map.pif-in.v1 / psi-in.v1 (native: InwardPifParser, InwardPsiParser); the agent leg | ported, source unit tests; leg on all engines; PSI map not wired (I-84) |
| `wps.RaiseInwardPsiException` | `WpsPlatformActivities.InwardPsiRaise.cs` | uae.wps | uae.wps.map.pif-in.v1 / psi-in.v1 (native: InwardPifParser, InwardPsiParser); the agent leg | ported, source unit tests; leg on all engines; PSI map not wired (I-84) |
| `wps.RaiseRestrictedPostingStuck` | `WpsPlatformActivities.RbDispatch.cs` | p; b values | p.decide.await-posting-window.v1 (schedule-gated await; native: PostingWindow) | gate ported; await in the inward legs on all engines |
| `wps.RaiseRfaNakItem` | `WpsPlatformActivities.RfaNak.cs` | uae.wps | uae.wps.map.ack-in and the leg's NAK path | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| `wps.ReconcilePosting` | `WpsPlatformActivities.Names.cs` | p via core-banking port; b binding | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking | built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116 |
| `wps.RecordPostingIndeterminate` | `WpsPlatformActivities.CoreBanking.cs` | p via core-banking port; b binding | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking | built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116 |
| `wps.RecordPostingStatus` | `WpsPlatformActivities.Names.cs` | p via core-banking port; b binding | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking | built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116 |
| `wps.RepostFailedDeUploads` | `WpsPlatformActivities.DeRepost.cs` | p core-banking adapter (bulk); b binding | the file-bulk adapter of p.port.core-banking.v1, not a WPS step (R-12-15) | built (FileBulkCoreBankingTests); one file per batch: I-132 |
| `wps.ResolveAndVerifyCorporateRegistrationByMolEstablishmentId` | `WpsPlatformActivities.cs` | p (intake) + uae.wps (MOL) | File API intake and front door (R-09-82, R-09-85); the corporate register and the WPS match (R-12-27) | MatchCorporate ported, golden; intake built |
| `wps.RetrieveAccountAndFileMetadataForPosting` | `WpsPlatformActivities.cs` | p | posting preparation inside p.post.accounting, read from the store by id | studio factory activities run on Temporal; source parity: P2 |
| `wps.RetrieveBranchCodeAndPostingConfiguration` | `WpsPlatformActivities.cs` | p via core-banking port; b binding | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking | built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116 |
| `wps.ReverseCorePosting` | `WpsPlatformActivities.CoreBanking.cs` | p via core-banking port; b binding | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking | built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116 |
| `wps.ReviewCbPosting` | `WpsPlatformActivities.CbPostingReview.cs` | p via core-banking port; b binding | p.post.accounting.v1 through the binding, in the posting window | on the runtime (CoreBankingPostingTests, e2e-wps-core-banking) |
| `wps.ReviewPrcNak` | `WpsPlatformActivities.PrcNak.cs` | uae.wps | uae.wps.map.ack-in and the leg's NAK path | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| `wps.ReviewRestrictedPosting` | `WpsPlatformActivities.RbReview.cs` | p; b values | p.decide.await-posting-window.v1 (schedule-gated await; native: PostingWindow) | gate ported; await in the inward legs on all engines |
| `wps.SaveToSalaryFileUploadQueueTableForProcessing` | `WpsPlatformActivities.cs` | p (intake) | File API intake; the file kept in the store with its signed hash (R-09-82) | built: File API (FileApiTests) |
| `wps.SaveUiUploadedSifFileToLocalPath` | `WpsPlatformActivities.cs` | p (intake) + uae.wps (MOL) | File API intake and front door (R-09-82, R-09-85); the corporate register and the WPS match (R-12-27) | MatchCorporate ported, golden; intake built |
| `wps.SaveUploadRequestToAuditQueue` | `WpsPlatformActivities.cs` | p (intake) + uae.wps (MOL) | File API intake and front door (R-09-82, R-09-85); the corporate register and the WPS match (R-12-27) | MatchCorporate ported, golden; intake built |
| `wps.SelectCbPostingCandidates` | `WpsPlatformActivities.CbPostingDispatch.cs` | p via core-banking port; b binding | p.post.accounting.v1 through the binding, in the posting window | on the runtime (CoreBankingPostingTests, e2e-wps-core-banking) |
| `wps.SelectRestrictedPostingCandidates` | `WpsPlatformActivities.RbDispatch.cs` | p; b values | p.decide.await-posting-window.v1 (schedule-gated await; native: PostingWindow) | gate ported; await in the inward legs on all engines |
| `wps.SubmitDeBulkUpload` | `WpsPlatformActivities.DeSubmit.cs` | p core-banking adapter (bulk); b binding | the file-bulk adapter of p.port.core-banking.v1, not a WPS step (R-12-15) | built (FileBulkCoreBankingTests); one file per batch: I-132 |
| `wps.SubmitSifToCb` | `WpsPlatformActivities.CentralBank.cs` | uae.p + uae.wps | uae.wps.exchange.send-salary-file through the File API outbox and the sftp adapter (R-09-84, R-09-86) | built (SftpTests); host keys: I-161 |
| `wps.TransferDeBulkFile` | `WpsPlatformActivities.DeTransfer.cs` | p core-banking adapter (bulk); b binding | the file-bulk adapter of p.port.core-banking.v1, not a WPS step (R-12-15) | built (FileBulkCoreBankingTests); one file per batch: I-132 |
| `wps.UpdateFileStatusBasedOnPostingResult` | `WpsPlatformActivities.cs` | p (store) | a status fact on the bulk; the status is a projection (insert-only) | store: status facts and projection |
| `wps.UploadDif` | `WpsPlatformActivities.DifUpload.cs` | uae.wps | uae.wps.map.dif-out.v2 (native: DifLines, DifFileBuilder); ACK/NAK uae.wps.map.ack-in.v1 | ported, source unit tests; leg on all engines |
| `wps.UploadPrc` | `WpsPlatformActivities.PrcUpload.cs` | uae.wps | uae.wps.map.prc-out.v2 (native: PrcFileBuilder, PrcFileNamePolicy); ACK/NAK uae.wps.map.ack-in.v1 | ported, source unit tests; leg on all engines |
| `wps.UploadRfa` | `WpsPlatformActivities.RfaUpload.cs` | uae.wps | uae.wps.map.rfa-out.v1 (native: RfaLines, RfaFileBuilder, RfaFileNamePolicy); ACK/NAK uae.wps.map.ack-in.v1 | ported, source unit tests; leg on all engines |
| `wps.ValidateFilenameDateComponentAndBusinessRules` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.validate.salary-file (CBX file-name rules 1–7) | rules ported, golden |
| `wps.ValidateSif` | `WpsPlatformActivities.Names.cs` | uae.wps | uae.wps.map.sif-in + uae.wps.validate.salary-file / salary-record | rules ported, golden; activity on the studio contract: P2 |
| `wps.ValidateSifFile` | `WpsPlatformActivities.SifValidation.cs` | uae.wps | uae.wps.map.sif-in + uae.wps.validate.salary-file / salary-record | rules ported, golden; activity on the studio contract: P2 |
| `wps.ValidateSifFilenameStructureAndFormat` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.validate.salary-file (CBX file-name rules 1–7) | rules ported, golden |

## Bank activities

Each becomes a bank value, hook or port binding of the shared flow; none survives as a bank activity.

| Bank | Activity | Becomes | Status |
| --- | --- | --- | --- |
| Adib | `AckPosting` | uae.wps.map.ack-in and the leg's NAK path (uae.wps) | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| Adib | `AdibDecryptSifFile` | p.protect.decrypt-file.v1 in the File API on arrival (R-09-83) (p) | built: openpgp and gnupg (EncryptionTests, FileApiTests) |
| Adib | `AdibSftpDownloadSifFile` | File API inboxes on the bank's file-transfer binding (R-09-82) (p) | built; inbox messages: I-160 |
| Adib | `AdibValidateWpsRules` | bank configuration (to decide) | P5 |
| Adib | `AdibWpsEmployerAmlCheck` | p.screen.sanctions / p.screen.fraud (p) | studio factory activities run on Temporal |
| Adib | `MohreNotification` | p.notify.channel; MOHRE notify as a bank hook (p.notify + uae.wps) | studio factory notify runs on Temporal; ministry status adapter built (R-12-16, I-118) |
| Adib | `NakReversalPosting` | uae.wps.map.ack-in and the leg's NAK path (uae.wps) | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| Adib | `ReceiveRtcFiles` | uae.wps.wf.return-in.v1 with uae.wps.map.rtc-in.v2 (payment-level creditor account; the source parses no RTC lines) (uae.wps) | on the runtime: map native, posting through the binding (WPS-RTC-001, NativeInwardTests) |
| Adib | `ReceiveSifAckNak` | uae.wps.map.ack-in and the leg's NAK path (uae.wps) | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| Adib | `ReverseWpsPosting` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116 |
| Adib | `RtcPosting` | uae.wps.wf.return-in.v1 with uae.wps.map.rtc-in.v2 (payment-level creditor account; the source parses no RTC lines) (uae.wps) | on the runtime: map native, posting through the binding (WPS-RTC-001, NativeInwardTests) |
| Adib | `UploadSifToCbSftp` | uae.wps.exchange.send-salary-file through the File API outbox and the sftp adapter (R-09-84, R-09-86) (uae.p + uae.wps) | built (SftpTests); host keys: I-161 |
| Adib | `WpsCoreBankingPosting` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116 |
| Dib | `DibDecryptSifFile` | p.protect.decrypt-file.v1 in the File API on arrival (R-09-83) (p) | built: openpgp and gnupg (EncryptionTests, FileApiTests) |
| Dib | `DibMohreNotifyResult` | p.notify.channel; MOHRE notify as a bank hook (p.notify + uae.wps) | studio factory notify runs on Temporal; ministry status adapter built (R-12-16, I-118) |
| Dib | `DibReverseWpsPosting` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116 |
| Dib | `DibSftpDownloadSifFile` | File API inboxes on the bank's file-transfer binding (R-09-82) (p) | built; inbox messages: I-160 |
| Dib | `DibSftpUploadWpsResult` | uae.wps.exchange.send-salary-file through the File API outbox and the sftp adapter (R-09-84, R-09-86) (uae.p + uae.wps) | built (SftpTests); host keys: I-161 |
| Dib | `DibValidateWpsRules` | bank configuration (to decide) | P5 |
| Dib | `DibWpsCoreBankingPosting` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116 |
| Dib | `DibWpsEmployerAmlCheck` | p.screen.sanctions / p.screen.fraud (p) | studio factory activities run on Temporal |
| Dib | `dib.BalanceEnquiry` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116 |
| Dib | `dib.CorePosting` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116 |
| Dib | `dib.ExecuteBalanceEnquiryOnCorporateAccount` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116 |
| Dib | `dib.ExecuteMultiTransactionPostingToCoreBanking` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116 |
| Dib | `dib.ExecuteSifNakFinancialReversalPostingToCoreBankingSystem` | uae.wps.map.ack-in and the leg's NAK path (uae.wps) | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| Dib | `dib.GenerateAndTransferSifFileToCentralBankViaSftp` | uae.wps.map.sif-out (CentralBankSif from the ISO 20022 store) (uae.wps) | ported, golden; generated from the store |
| Dib | `dib.QueryCoreBankingPostingStatus` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116 |
| Dib | `dib.RecordPostingIndeterminateForManualReconciliation` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116 |
| Dib | `dib.VerifyBalanceAndExecuteCoreBankingPosting` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116 |
| Enbd | `EnbdDecryptSifFile` | p.protect.decrypt-file.v1 in the File API on arrival (R-09-83) (p) | built: openpgp and gnupg (EncryptionTests, FileApiTests) |
| Enbd | `EnbdMohreNotifyResult` | p.notify.channel; MOHRE notify as a bank hook (p.notify + uae.wps) | studio factory notify runs on Temporal; ministry status adapter built (R-12-16, I-118) |
| Enbd | `EnbdReverseWpsPosting` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116 |
| Enbd | `EnbdSftpDownloadSifFile` | File API inboxes on the bank's file-transfer binding (R-09-82) (p) | built; inbox messages: I-160 |
| Enbd | `EnbdSftpUploadWpsResult` | uae.wps.exchange.send-salary-file through the File API outbox and the sftp adapter (R-09-84, R-09-86) (uae.p + uae.wps) | built (SftpTests); host keys: I-161 |
| Enbd | `EnbdValidateWpsRules` | bank configuration (to decide) | P5 |
| Enbd | `EnbdWpsCoreBankingPosting` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116 |
| Enbd | `EnbdWpsEmployerAmlCheck` | p.screen.sanctions / p.screen.fraud (p) | studio factory activities run on Temporal |
| Fab | `FabDecryptSifFile` | p.protect.decrypt-file.v1 in the File API on arrival (R-09-83) (p) | built: openpgp and gnupg (EncryptionTests, FileApiTests) |
| Fab | `FabMohreNotifyResult` | p.notify.channel; MOHRE notify as a bank hook (p.notify + uae.wps) | studio factory notify runs on Temporal; ministry status adapter built (R-12-16, I-118) |
| Fab | `FabReverseWpsPosting` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116 |
| Fab | `FabSftpDownloadSifFile` | File API inboxes on the bank's file-transfer binding (R-09-82) (p) | built; inbox messages: I-160 |
| Fab | `FabSftpUploadWpsResult` | uae.wps.exchange.send-salary-file through the File API outbox and the sftp adapter (R-09-84, R-09-86) (uae.p + uae.wps) | built (SftpTests); host keys: I-161 |
| Fab | `FabValidateWpsRules` | bank configuration (to decide) | P5 |
| Fab | `FabWpsCoreBankingPosting` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116 |
| Fab | `FabWpsEmployerAmlCheck` | p.screen.sanctions / p.screen.fraud (p) | studio factory activities run on Temporal |

## Totals

| Status | Platform activities |
| --- | --- |
| ported | 14 |
| rules ported | 13 |
| ACK/NAK file ported | 13 |
| built: simulator | 12 |
| gate ported | 7 |
| built (FileBulkCoreBankingTests) | 6 |
| MatchCorporate ported | 6 |
| hooks run on Temporal (kit CK-HOOK-01..04) | 4 |
| on the runtime (CoreBankingPostingTests | 3 |
| studio factory activity | 3 |
| no feed: I-189 | 3 |
| built | 2 |
| error codes kept in the ported rules | 2 |
| studio factory notify runs on Temporal | 2 |
| studio front door runs on Temporal | 2 |
| built: File API (FileApiTests) | 2 |
| studio factory activities run on Temporal | 2 |
| on the runtime (e2e-wps-inward-native) | 1 |
| built (SftpTests) | 1 |
| on the runtime: map native | 1 |
| store: status facts and projection | 1 |
| built: openpgp and gnupg (EncryptionTests | 1 |
