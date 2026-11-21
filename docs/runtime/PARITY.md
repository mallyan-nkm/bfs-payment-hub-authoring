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

Not yet ported (P2): the SSM structure checks inline in `WpsPlatformActivities.cs`, the DE, DIF, RFA, PRC and RRI rule classes, `WpsEmployeeBankClassifier`, `AccTxnDecisionRules`, `RbPostingClassifier`.

## Platform activities

| Activity | Source file | Target layer | Target | Status |
| --- | --- | --- | --- | --- |
| `EvaluateExchangeRateStaleness` | `ExchangeRateRefreshActivities.cs` | p | p exchange-rate refresh (not WPS) | P2 |
| `GenerateSifFile` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.map.sif-out (CentralBankSif from the ISO 20022 store) | ported, golden; generated from the store |
| `ParseRtcRecords` | `WpsPlatformActivities.cs` | uae.wps | uae.wps RTC flow (ADIB posting as a hook) | P2 |
| `ParseSifRecords` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.map.sif-in + uae.wps.validate.salary-file / salary-record | rules ported, golden; activity on the studio contract: P2 |
| `RecordExchangeRateRunOutcome` | `ExchangeRateRefreshActivities.cs` | p | p exchange-rate refresh (not WPS) | P2 |
| `RefreshExchangeRates` | `ExchangeRateRefreshActivities.cs` | p | p exchange-rate refresh (not WPS) | P2 |
| `RouteToNonWpsHandler` | `WpsPlatformActivities.cs` | p | front door workflow-select | studio front door runs on Temporal |
| `RouteWpsPayment` | `WpsPlatformActivities.cs` | p | front door workflow-select | studio front door runs on Temporal |
| `ValidateWpsSifFile` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.map.sif-in + uae.wps.validate.salary-file / salary-record | rules ported, golden; activity on the studio contract: P2 |
| `Wps.Hook.PostCorePosting` | `WpsHookActivities.cs` | p via core-banking port; b binding | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking | studio factory activities run on Temporal; bank adapter binding: P5 |
| `Wps.Hook.PostSettlement` | `WpsHookActivities.cs` | p hook points | workflow hook points with bank bindings | hooks run on Temporal (kit CK-HOOK-01..04) |
| `Wps.Hook.PostValidation` | `WpsHookActivities.cs` | p hook points | workflow hook points with bank bindings | hooks run on Temporal (kit CK-HOOK-01..04) |
| `Wps.Hook.PreCorePosting` | `WpsHookActivities.cs` | p via core-banking port; b binding | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking | studio factory activities run on Temporal; bank adapter binding: P5 |
| `Wps.Hook.PreFtsDispatch` | `WpsHookActivities.cs` | p hook points | workflow hook points with bank bindings | hooks run on Temporal (kit CK-HOOK-01..04) |
| `Wps.Hook.PreReturnResult` | `WpsHookActivities.cs` | p hook points | workflow hook points with bank bindings | hooks run on Temporal (kit CK-HOOK-01..04) |
| `wps.ApplyDifAck` | `WpsPlatformActivities.DifAck.cs` | uae.wps | uae.wps.map.ack-in and the leg's NAK path | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| `wps.ApplyDifNak` | `WpsPlatformActivities.DifNak.cs` | uae.wps | uae.wps.map.ack-in and the leg's NAK path | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| `wps.ApplyPrcAck` | `WpsPlatformActivities.PrcAck.cs` | uae.wps | uae.wps.map.ack-in and the leg's NAK path | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| `wps.ApplyPrcNak` | `WpsPlatformActivities.PrcNak.cs` | uae.wps | uae.wps.map.ack-in and the leg's NAK path | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| `wps.ApplyRfaAck` | `WpsPlatformActivities.RfaAck.cs` | uae.wps | uae.wps.map.ack-in and the leg's NAK path | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| `wps.ApplyRfaNak` | `WpsPlatformActivities.RfaNak.cs` | uae.wps | uae.wps.map.ack-in and the leg's NAK path | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| `wps.AwaitCbPostingWindow` | `WpsPlatformActivities.CbPostingAwait.cs` | p scheduler; b values | schedule-gated await (posting windows) | P2 |
| `wps.AwaitDifResponse` | `WpsPlatformActivities.InwardDifAwait.cs` | uae.wps | uae.wps inward DIF flow | P2 |
| `wps.AwaitPrcResponse` | `WpsPlatformActivities.InwardPrcAwait.cs` | uae.wps | uae.wps inward PRC flow | P2 |
| `wps.AwaitRestrictedPostingWindow` | `WpsPlatformActivities.RbAwait.cs` | p scheduler; b values | schedule-gated await (posting windows) | P2 |
| `wps.AwaitRfaResponse` | `WpsPlatformActivities.InwardRfaAwait.cs` | uae.wps | uae.wps RFA flow | P2 |
| `wps.BalanceEnquiry` | `WpsPlatformActivities.CoreBanking.cs` | p via core-banking port; b binding | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking | studio factory activities run on Temporal; bank adapter binding: P5 |
| `wps.BuildValidationResponse` | `WpsPlatformActivities.Names.cs` | p + uae.wps | the channel response (p.notify.channel) with the uae.wps error codes | error codes kept in the ported rules; response: P2 |
| `wps.CalculateCharges` | `WpsPlatformActivities.Charges.cs` | p + b values | p charges activity, slabs/VAT/narration as bank values | P2 |
| `wps.CheckDuplicateSif` | `WpsPlatformActivities.Names.cs` | p (pattern) + uae.wps (keys) | p.validate.duplicate with uae.wps keys | studio factory activity; source keys: P2 |
| `wps.CheckForDuplicateFilenameSubmission` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.validate.salary-file (CBX file-name rules 1–7) | rules ported, golden |
| `wps.CheckForDuplicatePostingAndPrepareCoreBankingRequest` | `WpsPlatformActivities.cs` | p (pattern) + uae.wps (keys) | p.validate.duplicate with uae.wps keys | studio factory activity; source keys: P2 |
| `wps.CheckForDuplicateSifFileSubmission` | `WpsPlatformActivities.cs` | p (pattern) + uae.wps (keys) | p.validate.duplicate with uae.wps keys | studio factory activity; source keys: P2 |
| `wps.CheckLostDeFiles` | `WpsPlatformActivities.LostFile.cs` | uae.wps; b port for upload | uae.wps DE bulk flow | P2 |
| `wps.ConsolidateValidationErrorsAndGenerateResponse` | `WpsPlatformActivities.cs` | p + uae.wps | the channel response (p.notify.channel) with the uae.wps error codes | error codes kept in the ported rules; response: P2 |
| `wps.ConstructChargePostingTransactions` | `WpsPlatformActivities.cs` | p + b values | p charges activity, slabs/VAT/narration as bank values | P2 |
| `wps.ConstructSalaryAmountPostingTransaction` | `WpsPlatformActivities.cs` | p via core-banking port; b binding | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking | studio factory activities run on Temporal; bank adapter binding: P5 |
| `wps.CorePosting` | `WpsPlatformActivities.CoreBanking.cs` | p via core-banking port; b binding | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking | studio factory activities run on Temporal; bank adapter binding: P5 |
| `wps.DecryptSif` | `WpsPlatformActivities.Decryption.cs` | p | p decrypt (file intake) | P2 |
| `wps.DetermineDeOutcome` | `WpsPlatformActivities.DeOutcome.cs` | uae.wps; b port for upload | uae.wps DE bulk flow | P2 |
| `wps.EvaluateCbCallWindow` | `WpsPlatformActivities.CbPostingWindow.cs` | p scheduler; b values | schedule-gated await (posting windows) | P2 |
| `wps.ExtractAndParseSifUploadRequestJson` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.map.sif-in + uae.wps.validate.salary-file / salary-record | rules ported, golden; activity on the studio contract: P2 |
| `wps.FetchSalaryFilesReadyForCoreBankingPosting` | `WpsPlatformActivities.cs` | p via core-banking port; b binding | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking | studio factory activities run on Temporal; bank adapter binding: P5 |
| `wps.GenerateCbxSif` | `WpsPlatformActivities.SifGeneration.cs` | uae.wps | uae.wps.map.sif-out (CentralBankSif from the ISO 20022 store) | ported, golden; generated from the store |
| `wps.GenerateDeBulkFile` | `WpsPlatformActivities.DeBulkFile.cs` | uae.wps; b port for upload | uae.wps DE bulk flow | P2 |
| `wps.GenerateInwardDifFile` | `WpsPlatformActivities.InwardDif.cs` | uae.wps | uae.wps inward DIF flow | P2 |
| `wps.GenerateInwardPrcFile` | `WpsPlatformActivities.InwardPrc.cs` | uae.wps | uae.wps inward PRC flow | P2 |
| `wps.InitializeAndValidateSifUploadRequest` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.map.sif-in + uae.wps.validate.salary-file / salary-record | rules ported, golden; activity on the studio contract: P2 |
| `wps.InitializePostingToNostroProcess` | `WpsPlatformActivities.cs` | p | posting preparation inside p.post.accounting, read from the store by id | studio factory activities run on Temporal; source parity: P2 |
| `wps.InitializeSifUploadRequestProcessing` | `WpsPlatformActivities.cs` | p (intake) + uae.wps (MOL) | front door intake; uae.wps MOL corporate match (SifUploadRules.MatchCorporate) | MatchCorporate ported, golden; intake: P2 |
| `wps.InitiateSifFileParsingAndBusinessValidation` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.map.sif-in + uae.wps.validate.salary-file / salary-record | rules ported, golden; activity on the studio contract: P2 |
| `wps.LocateAndReadSalaryFileFromConfiguredLocalPath` | `WpsPlatformActivities.cs` | p (intake) + uae.wps (MOL) | front door intake; uae.wps MOL corporate match (SifUploadRules.MatchCorporate) | MatchCorporate ported, golden; intake: P2 |
| `wps.NotifyChannelAccepted` | `WpsPlatformActivities.Names.cs` | p.notify + uae.wps | p.notify.channel; MOHRE notify as a bank hook | studio factory notify runs on Temporal; MOHRE: P5 |
| `wps.NotifyChannelRejected` | `WpsPlatformActivities.Names.cs` | p.notify + uae.wps | p.notify.channel; MOHRE notify as a bank hook | studio factory notify runs on Temporal; MOHRE: P5 |
| `wps.ParseAndQueueSifFile` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.map.sif-in + uae.wps.validate.salary-file / salary-record | rules ported, golden; activity on the studio contract: P2 |
| `wps.ParseAndSaveInwardPif` | `WpsPlatformActivities.InwardPif.cs` | uae.wps | uae.wps.map.pif-in, inward leg | studio factory map exists; source parity: P2 |
| `wps.ParseAndSaveInwardPsi` | `WpsPlatformActivities.InwardPsi.cs` | uae.wps | uae.wps.map.pif-in, inward leg | studio factory map exists; source parity: P2 |
| `wps.ParseJsonDocumentAndExtractSifUploadFields` | `WpsPlatformActivities.cs` | p (intake) | front door intake; the file kept as a message in the store | store: messages and accepted bulk (DurableClaimCheck); intake: P2 |
| `wps.ParseSif` | `WpsPlatformActivities.Names.cs` | uae.wps | uae.wps.map.sif-in + uae.wps.validate.salary-file / salary-record | rules ported, golden; activity on the studio contract: P2 |
| `wps.ParseSifControlRecordAndExtractMetadata` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.map.sif-in + uae.wps.validate.salary-file / salary-record | rules ported, golden; activity on the studio contract: P2 |
| `wps.PostRestrictedBankPayment` | `WpsPlatformActivities.RbPost.cs` | p scheduler; b values | schedule-gated await (posting windows) | P2 |
| `wps.PostRfaReversal` | `WpsPlatformActivities.RfaReversal.cs` | uae.wps | uae.wps RFA flow | P2 |
| `wps.ProcessCbResponse` | `WpsPlatformActivities.Names.cs` | uae.wps | uae.wps.map.ack-in and the leg's NAK path | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| `wps.ProcessCentralBankAcknowledgmentFilesAndUpdateSifStatus` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.map.ack-in and the leg's NAK path | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| `wps.ProcessSifAckResponseAndNotifyChannelOfAcceptance` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.map.ack-in and the leg's NAK path | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| `wps.ProcessSifNakResponseAndNotifyChannelOfRejection` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.map.ack-in and the leg's NAK path | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| `wps.QueryCorePostingStatus` | `WpsPlatformActivities.CoreBanking.cs` | p via core-banking port; b binding | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking | studio factory activities run on Temporal; bank adapter binding: P5 |
| `wps.QueueSif` | `WpsPlatformActivities.Names.cs` | p (intake) + uae.wps (MOL) | front door intake; uae.wps MOL corporate match (SifUploadRules.MatchCorporate) | MatchCorporate ported, golden; intake: P2 |
| `wps.RaiseCbPostingStuck` | `WpsPlatformActivities.CbPostingDispatch.cs` | p scheduler; b values | schedule-gated await (posting windows) | P2 |
| `wps.RaiseDifNakItem` | `WpsPlatformActivities.DifNak.cs` | uae.wps | uae.wps.map.ack-in and the leg's NAK path | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| `wps.RaiseInwardPifException` | `WpsPlatformActivities.InwardPifRaise.cs` | uae.wps | uae.wps.map.pif-in, inward leg | studio factory map exists; source parity: P2 |
| `wps.RaiseInwardPsiException` | `WpsPlatformActivities.InwardPsiRaise.cs` | uae.wps | uae.wps.map.pif-in, inward leg | studio factory map exists; source parity: P2 |
| `wps.RaiseRestrictedPostingStuck` | `WpsPlatformActivities.RbDispatch.cs` | p scheduler; b values | schedule-gated await (posting windows) | P2 |
| `wps.RaiseRfaNakItem` | `WpsPlatformActivities.RfaNak.cs` | uae.wps | uae.wps.map.ack-in and the leg's NAK path | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| `wps.ReconcilePosting` | `WpsPlatformActivities.Names.cs` | p via core-banking port; b binding | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking | studio factory activities run on Temporal; bank adapter binding: P5 |
| `wps.RecordPostingIndeterminate` | `WpsPlatformActivities.CoreBanking.cs` | p via core-banking port; b binding | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking | studio factory activities run on Temporal; bank adapter binding: P5 |
| `wps.RecordPostingStatus` | `WpsPlatformActivities.Names.cs` | p via core-banking port; b binding | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking | studio factory activities run on Temporal; bank adapter binding: P5 |
| `wps.RepostFailedDeUploads` | `WpsPlatformActivities.DeRepost.cs` | uae.wps; b port for upload | uae.wps DE bulk flow | P2 |
| `wps.ResolveAndVerifyCorporateRegistrationByMolEstablishmentId` | `WpsPlatformActivities.cs` | p (intake) + uae.wps (MOL) | front door intake; uae.wps MOL corporate match (SifUploadRules.MatchCorporate) | MatchCorporate ported, golden; intake: P2 |
| `wps.RetrieveAccountAndFileMetadataForPosting` | `WpsPlatformActivities.cs` | p | posting preparation inside p.post.accounting, read from the store by id | studio factory activities run on Temporal; source parity: P2 |
| `wps.RetrieveBranchCodeAndPostingConfiguration` | `WpsPlatformActivities.cs` | p via core-banking port; b binding | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking | studio factory activities run on Temporal; bank adapter binding: P5 |
| `wps.ReverseCorePosting` | `WpsPlatformActivities.CoreBanking.cs` | p via core-banking port; b binding | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking | studio factory activities run on Temporal; bank adapter binding: P5 |
| `wps.ReviewCbPosting` | `WpsPlatformActivities.CbPostingReview.cs` | p scheduler; b values | schedule-gated await (posting windows) | P2 |
| `wps.ReviewPrcNak` | `WpsPlatformActivities.PrcNak.cs` | uae.wps | uae.wps.map.ack-in and the leg's NAK path | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| `wps.ReviewRestrictedPosting` | `WpsPlatformActivities.RbReview.cs` | p scheduler; b values | schedule-gated await (posting windows) | P2 |
| `wps.SaveToSalaryFileUploadQueueTableForProcessing` | `WpsPlatformActivities.cs` | p (intake) | front door intake; the file kept as a message in the store | store: messages and accepted bulk (DurableClaimCheck); intake: P2 |
| `wps.SaveUiUploadedSifFileToLocalPath` | `WpsPlatformActivities.cs` | p (intake) + uae.wps (MOL) | front door intake; uae.wps MOL corporate match (SifUploadRules.MatchCorporate) | MatchCorporate ported, golden; intake: P2 |
| `wps.SaveUploadRequestToAuditQueue` | `WpsPlatformActivities.cs` | p (intake) + uae.wps (MOL) | front door intake; uae.wps MOL corporate match (SifUploadRules.MatchCorporate) | MatchCorporate ported, golden; intake: P2 |
| `wps.SelectCbPostingCandidates` | `WpsPlatformActivities.CbPostingDispatch.cs` | p scheduler; b values | schedule-gated await (posting windows) | P2 |
| `wps.SelectRestrictedPostingCandidates` | `WpsPlatformActivities.RbDispatch.cs` | p scheduler; b values | schedule-gated await (posting windows) | P2 |
| `wps.SubmitDeBulkUpload` | `WpsPlatformActivities.DeSubmit.cs` | uae.wps; b port for upload | uae.wps DE bulk flow | P2 |
| `wps.SubmitSifToCb` | `WpsPlatformActivities.CentralBank.cs` | uae.p + uae.wps | uae.wps.exchange.send-salary-file over the uae.p CB SFTP port | studio factory activity runs on Temporal; source transport: P2 |
| `wps.TransferDeBulkFile` | `WpsPlatformActivities.DeTransfer.cs` | uae.wps; b port for upload | uae.wps DE bulk flow | P2 |
| `wps.UpdateFileStatusBasedOnPostingResult` | `WpsPlatformActivities.cs` | p (store) | a status fact on the bulk; the status is a projection (insert-only) | store: status facts and projection |
| `wps.UploadDif` | `WpsPlatformActivities.DifUpload.cs` | uae.wps | uae.wps inward DIF flow | P2 |
| `wps.UploadPrc` | `WpsPlatformActivities.PrcUpload.cs` | uae.wps | uae.wps inward PRC flow | P2 |
| `wps.UploadRfa` | `WpsPlatformActivities.RfaUpload.cs` | uae.wps | uae.wps RFA flow | P2 |
| `wps.ValidateFilenameDateComponentAndBusinessRules` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.validate.salary-file (CBX file-name rules 1–7) | rules ported, golden |
| `wps.ValidateSif` | `WpsPlatformActivities.Names.cs` | uae.wps | uae.wps.map.sif-in + uae.wps.validate.salary-file / salary-record | rules ported, golden; activity on the studio contract: P2 |
| `wps.ValidateSifFile` | `WpsPlatformActivities.SifValidation.cs` | uae.wps | uae.wps.map.sif-in + uae.wps.validate.salary-file / salary-record | rules ported, golden; activity on the studio contract: P2 |
| `wps.ValidateSifFilenameStructureAndFormat` | `WpsPlatformActivities.cs` | uae.wps | uae.wps.validate.salary-file (CBX file-name rules 1–7) | rules ported, golden |

## Bank activities

Each becomes a bank value, hook or port binding of the shared flow; none survives as a bank activity.

| Bank | Activity | Becomes | Status |
| --- | --- | --- | --- |
| Adib | `AckPosting` | uae.wps.map.ack-in and the leg's NAK path (uae.wps) | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| Adib | `AdibDecryptSifFile` | p decrypt (file intake) (p) | P5 |
| Adib | `AdibSftpDownloadSifFile` | file intake watcher (p) | P5 |
| Adib | `AdibValidateWpsRules` | bank configuration (to decide) | P5 |
| Adib | `AdibWpsEmployerAmlCheck` | p.screen.sanctions / p.screen.fraud (p) | studio factory activities run on Temporal |
| Adib | `MohreNotification` | p.notify.channel; MOHRE notify as a bank hook (p.notify + uae.wps) | studio factory notify runs on Temporal; MOHRE: P5 |
| Adib | `NakReversalPosting` | uae.wps.map.ack-in and the leg's NAK path (uae.wps) | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| Adib | `ReceiveRtcFiles` | uae.wps RTC flow (ADIB posting as a hook) (uae.wps) | P5 |
| Adib | `ReceiveSifAckNak` | uae.wps.map.ack-in and the leg's NAK path (uae.wps) | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| Adib | `ReverseWpsPosting` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | studio factory activities run on Temporal; bank adapter binding: P5 |
| Adib | `RtcPosting` | uae.wps RTC flow (ADIB posting as a hook) (uae.wps) | P5 |
| Adib | `UploadSifToCbSftp` | uae.wps.exchange.send-salary-file over the uae.p CB SFTP port (uae.p + uae.wps) | studio factory activity runs on Temporal; source transport: P2 |
| Adib | `WpsCoreBankingPosting` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | studio factory activities run on Temporal; bank adapter binding: P5 |
| Dib | `DibDecryptSifFile` | p decrypt (file intake) (p) | P5 |
| Dib | `DibMohreNotifyResult` | p.notify.channel; MOHRE notify as a bank hook (p.notify + uae.wps) | studio factory notify runs on Temporal; MOHRE: P5 |
| Dib | `DibReverseWpsPosting` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | studio factory activities run on Temporal; bank adapter binding: P5 |
| Dib | `DibSftpDownloadSifFile` | file intake watcher (p) | P5 |
| Dib | `DibSftpUploadWpsResult` | uae.wps.exchange.send-salary-file over the uae.p CB SFTP port (uae.p + uae.wps) | studio factory activity runs on Temporal; source transport: P2 |
| Dib | `DibValidateWpsRules` | bank configuration (to decide) | P5 |
| Dib | `DibWpsCoreBankingPosting` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | studio factory activities run on Temporal; bank adapter binding: P5 |
| Dib | `DibWpsEmployerAmlCheck` | p.screen.sanctions / p.screen.fraud (p) | studio factory activities run on Temporal |
| Dib | `dib.BalanceEnquiry` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | studio factory activities run on Temporal; bank adapter binding: P5 |
| Dib | `dib.CorePosting` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | studio factory activities run on Temporal; bank adapter binding: P5 |
| Dib | `dib.ExecuteBalanceEnquiryOnCorporateAccount` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | studio factory activities run on Temporal; bank adapter binding: P5 |
| Dib | `dib.ExecuteMultiTransactionPostingToCoreBanking` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | studio factory activities run on Temporal; bank adapter binding: P5 |
| Dib | `dib.ExecuteSifNakFinancialReversalPostingToCoreBankingSystem` | uae.wps.map.ack-in and the leg's NAK path (uae.wps) | ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2 |
| Dib | `dib.GenerateAndTransferSifFileToCentralBankViaSftp` | uae.wps.map.sif-out (CentralBankSif from the ISO 20022 store) (uae.wps) | ported, golden; generated from the store |
| Dib | `dib.QueryCoreBankingPostingStatus` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | studio factory activities run on Temporal; bank adapter binding: P5 |
| Dib | `dib.RecordPostingIndeterminateForManualReconciliation` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | studio factory activities run on Temporal; bank adapter binding: P5 |
| Dib | `dib.VerifyBalanceAndExecuteCoreBankingPosting` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | studio factory activities run on Temporal; bank adapter binding: P5 |
| Enbd | `EnbdDecryptSifFile` | p decrypt (file intake) (p) | P5 |
| Enbd | `EnbdMohreNotifyResult` | p.notify.channel; MOHRE notify as a bank hook (p.notify + uae.wps) | studio factory notify runs on Temporal; MOHRE: P5 |
| Enbd | `EnbdReverseWpsPosting` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | studio factory activities run on Temporal; bank adapter binding: P5 |
| Enbd | `EnbdSftpDownloadSifFile` | file intake watcher (p) | P5 |
| Enbd | `EnbdSftpUploadWpsResult` | uae.wps.exchange.send-salary-file over the uae.p CB SFTP port (uae.p + uae.wps) | studio factory activity runs on Temporal; source transport: P2 |
| Enbd | `EnbdValidateWpsRules` | bank configuration (to decide) | P5 |
| Enbd | `EnbdWpsCoreBankingPosting` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | studio factory activities run on Temporal; bank adapter binding: P5 |
| Enbd | `EnbdWpsEmployerAmlCheck` | p.screen.sanctions / p.screen.fraud (p) | studio factory activities run on Temporal |
| Fab | `FabDecryptSifFile` | p decrypt (file intake) (p) | P5 |
| Fab | `FabMohreNotifyResult` | p.notify.channel; MOHRE notify as a bank hook (p.notify + uae.wps) | studio factory notify runs on Temporal; MOHRE: P5 |
| Fab | `FabReverseWpsPosting` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | studio factory activities run on Temporal; bank adapter binding: P5 |
| Fab | `FabSftpDownloadSifFile` | file intake watcher (p) | P5 |
| Fab | `FabSftpUploadWpsResult` | uae.wps.exchange.send-salary-file over the uae.p CB SFTP port (uae.p + uae.wps) | studio factory activity runs on Temporal; source transport: P2 |
| Fab | `FabValidateWpsRules` | bank configuration (to decide) | P5 |
| Fab | `FabWpsCoreBankingPosting` | p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking (p via core-banking port; b binding) | studio factory activities run on Temporal; bank adapter binding: P5 |
| Fab | `FabWpsEmployerAmlCheck` | p.screen.sanctions / p.screen.fraud (p) | studio factory activities run on Temporal |

## Totals

| Status | Platform activities |
| --- | --- |
| P2 | 32 |
| studio factory activities run on Temporal | 14 |
| rules ported | 13 |
| ACK/NAK file ported | 13 |
| MatchCorporate ported | 6 |
| studio factory map exists | 4 |
| hooks run on Temporal (kit CK-HOOK-01..04) | 4 |
| studio factory activity | 3 |
| ported | 2 |
| error codes kept in the ported rules | 2 |
| studio factory notify runs on Temporal | 2 |
| studio front door runs on Temporal | 2 |
| store: messages and accepted bulk (DurableClaimCheck) | 2 |
| studio factory activity runs on Temporal | 1 |
| store: status facts and projection | 1 |
