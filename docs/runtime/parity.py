# Builds docs/runtime/PARITY.md from the TransactHub source: every registered WPS activity (platform and bank),
# where it goes in the north-star layout, and what proves it today. Usage: python3 -I parity.py <src> <out.md>
import os, re, sys
src, out = sys.argv[1], sys.argv[2]
pat = re.compile(r'\[PaymentActivity\("([^"]+)"')
def scan(root):
    found = {}
    for d, _, fs in os.walk(root):
        if '/bin/' in d + '/' or '/obj/' in d + '/': continue
        for f in fs:
            if f.endswith('.cs'):
                for n in pat.findall(open(os.path.join(d, f), encoding='utf-8', errors='replace').read()):
                    found.setdefault(n, os.path.relpath(os.path.join(d, f), src))
    return found
plat = scan(os.path.join(src, 'services/activities/platform/PaymentHub.Activities.Platform.Wps'))
plat.update(scan(os.path.join(src, 'services/activities/platform/PaymentHub.Wps.InwardRri.Rules')))
bank = {}
for b in sorted(os.listdir(os.path.join(src, 'services/activities/bank'))):
    for n, f in scan(os.path.join(src, 'services/activities/bank', b)).items():
        if re.search(r'wps|sif|mohre|rtc|salary', n + f, re.I): bank[n] = (b, f)

# (pattern, target layer, target, status)
RULES = [
    (r'BuildValidationResponse|ConsolidateValidationErrors', 'p + uae.wps', 'the channel response (p.notify.channel) with the uae.wps error codes', 'error codes kept in the ported rules; response: P2'),
    (r'InitializePostingToNostro|RetrieveAccountAndFileMetadataForPosting', 'p', 'posting preparation inside p.post.accounting, read from the store by id', 'studio factory activities run on Temporal; source parity: P2'),
    (r'ParseJsonDocument|SaveToSalaryFileUploadQueue', 'p (intake)', 'File API intake; the file kept in the store with its signed hash (R-09-82)', 'built: File API (FileApiTests)'),
    (r'UpdateFileStatus', 'p (store)', 'a status fact on the bulk; the status is a projection (insert-only)', 'store: status facts and projection'),
    (r'(Validate|Check).*Filename|FilenameDate|ValidateSifFilename', 'uae.wps', 'uae.wps.validate.salary-file (CBX file-name rules 1–7)', 'rules ported, golden'),
    (r'ParseSif|ValidateSif|InitiateSifFileParsing|ParseSifControl|ValidateWpsSifFile|ParseSifRecords|ParseAndQueueSifFile', 'uae.wps', 'uae.wps.map.sif-in + uae.wps.validate.salary-file / salary-record', 'rules ported, golden; activity on the studio contract: P2'),
    (r'GenerateCbxSif|GenerateSifFile|GenerateAndTransferSif', 'uae.wps', 'uae.wps.map.sif-out (CentralBankSif from the ISO 20022 store)', 'ported, golden; generated from the store'),
    (r'SubmitSifToCb|UploadSifToCbSftp|SftpUpload|TransferSif', 'uae.p + uae.wps', 'uae.wps.exchange.send-salary-file through the File API outbox and the sftp adapter (R-09-84, R-09-86)', 'built (SftpTests); host keys: I-161'),
    (r'ProcessCbResponse|ReceiveSifAckNak|Ack|Nak', 'uae.wps', 'uae.wps.map.ack-in and the leg\'s NAK path', 'ACK/NAK file ported, golden; studio factory flow runs on Temporal; activity: P2'),
    (r'Duplicate', 'p (pattern) + uae.wps (keys)', 'p.validate.duplicate with uae.wps keys', 'studio factory activity; source keys: P2'),
    (r'Approval|Quorum|PaymentQueue', 'p', 'p.hold.approval / operations queue', 'studio factory activity; source policy rules: P2'),
    (r'Charge', 'p + b values', 'p.enrich.charges.v1 on the bank\'s slabs, posted by p.post.accounting.v2 (R-12-23, R-12-25)', 'built, native code-behind (PlatformNativeTests); DIB slabs: I-58'),
    (r'BalanceEnquiry|CorePosting|ReverseCorePosting|PostingStatus|ReconcilePosting|PostingIndeterminate|RecordPostingStatus|ConstructSalaryAmountPosting|RetrieveBranchCode|CoreBanking|WpsCoreBankingPosting|ReverseWpsPosting|ReversalPosting', 'p via core-banking port; b binding', 'p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking', 'built: simulator, gateway-rest and file-bulk bindings (CoreBankingPostingTests); bank settings: I-85, I-116'),
    (r'Decrypt', 'p', 'p.protect.decrypt-file.v1 in the File API on arrival (R-09-83)', 'built: openpgp and gnupg (EncryptionTests, FileApiTests)'),
    (r'Corporate|MolEstablishment|Precheck|UploadRequest|SaveUiUploaded|LocateAndReadSalaryFile|InitializeSifUpload|ExtractAndParseSifUpload|AuditQueue|QueueSif', 'p (intake) + uae.wps (MOL)', 'File API intake and front door (R-09-82, R-09-85); the corporate register and the WPS match (R-12-27)', 'MatchCorporate ported, golden; intake built'),
    (r'Pif|Psi', 'uae.wps', 'uae.wps.map.pif-in.v1 / psi-in.v1 (native: InwardPifParser, InwardPsiParser); the agent leg', 'ported, source unit tests; leg on all engines; PSI map not wired (I-84)'),
    (r'Prc', 'uae.wps', 'uae.wps.map.prc-out.v2 (native: PrcFileBuilder, PrcFileNamePolicy); ACK/NAK uae.wps.map.ack-in.v1', 'ported, source unit tests; leg on all engines'),
    (r'Dif', 'uae.wps', 'uae.wps.map.dif-out.v2 (native: DifLines, DifFileBuilder); ACK/NAK uae.wps.map.ack-in.v1', 'ported, source unit tests; leg on all engines'),
    (r'RfaReversal', 'uae.wps via core-banking port', 'uae.wps.post.return.v1 (suspense to settlement, the returns decision amount)', 'on the runtime (e2e-wps-inward-native)'),
    (r'Rfa', 'uae.wps', 'uae.wps.map.rfa-out.v1 (native: RfaLines, RfaFileBuilder, RfaFileNamePolicy); ACK/NAK uae.wps.map.ack-in.v1', 'ported, source unit tests; leg on all engines'),
    (r'Rri', 'uae.wps; core-banking block', 'uae.wps.wf.refund-request-in.v1 on the refund order p.wf.refund.v1 (native: InwardRriParser; I-133)', 'built (WPS-RRI tests); open questions: I-134'),
    (r'DeBulk|DeOutcome|DeUpload|RepostFailedDe|LostDe|DetermineDe|SubmitDe|TransferDe', 'p core-banking adapter (bulk); b binding', 'the file-bulk adapter of p.port.core-banking.v1, not a WPS step (R-12-15)', 'built (FileBulkCoreBankingTests); one file per batch: I-132'),
    (r'CbCallWindow|PostingWindow|Restricted|Rb[A-Z]|CbPostingAwait|AwaitCbPosting', 'p; b values', 'p.decide.await-posting-window.v1 (schedule-gated await; native: PostingWindow)', 'gate ported; await in the inward legs on all engines'),
    (r'CbPosting', 'p via core-banking port; b binding', 'p.post.accounting.v1 through the binding, in the posting window', 'on the runtime (CoreBankingPostingTests, e2e-wps-core-banking)'),
    (r'ExchangeRate', 'p', 'p exchange-rate refresh (not WPS); rates are a dial of p.enrich.fx-rate.v1', 'no feed: I-189'),
    (r'Hook', 'p hook points', 'workflow hook points with bank bindings', 'hooks run on Temporal (kit CK-HOOK-01..04)'),
    (r'Rtc', 'uae.wps', 'uae.wps.wf.return-in.v1 with uae.wps.map.rtc-in.v2 (payment-level creditor account; the source parses no RTC lines)', 'on the runtime: map native, posting through the binding (WPS-RTC-001, NativeInwardTests)'),
    (r'Mohre|Notify', 'p.notify + uae.wps', 'p.notify.channel; MOHRE notify as a bank hook', 'studio factory notify runs on Temporal; ministry status adapter built (R-12-16, I-118)'),
    (r'Aml|Screen', 'p', 'p.screen.sanctions / p.screen.fraud', 'studio factory activities run on Temporal'),
    (r'Route', 'p', 'front door workflow-select', 'studio front door runs on Temporal'),
    (r'SftpDownload', 'p', 'File API inboxes on the bank\'s file-transfer binding (R-09-82)', 'built; inbox messages: I-160'),
]
def classify(n):
    for p, layer, target, status in RULES:
        if re.search(p, n): return layer, target, status
    return 'to decide', '', 'P2: not yet mapped'

lines = ['# WPS parity matrix', '',
 'Generated from the TransactHub source by `docs/runtime/parity.py`. Every activity the source registers for WPS, on the platform and in each bank, with where it goes in the north-star layout and what proves it today. "Golden" means the runtime\'s port answers a corpus generated from the source\'s own code exactly (`runtime/tests/TransactHub.Store.Tests/Golden`).', '',
 f'Platform WPS activities: {len(plat)}. Bank WPS activities: {len(bank)} (every one is removed: a bank is configuration, hooks and port bindings).', '',
 '## Rule classes', '', '| Source | Runtime | Proof |', '| --- | --- | --- |']
for c in ['CbxFileNameRules', 'CbxRecordRules', 'CbxScrRules', 'CbxRuleSettings', 'SifUploadRules (file name, date, corporate match)', 'CbxSifWriter', 'WpsAckNakFile (ACK/NAK name, parse, build)', 'WpsEmployeeBankClassifier', 'WpsRoutingCode', 'AccountMasking']:
    lines.append(f'| `{c}` | `TransactHub.Activities.Wps` | golden (1,977 cases in all) |')
for c in ['InwardFileText', 'InwardPifParser', 'InwardPsiParser', 'PrcFileBuilder, PrcFileNamePolicy, PrcNakFileReader', 'DifLines, DifFileBuilder, DifAmountRule, DifNakLineResolver', 'RfaLines, RfaFileBuilder, RfaFileNamePolicy, RfaAmountRule', 'InwardRriParser']:
    lines.append(f'| `{c}` | `TransactHub.Activities.Wps.Inward` | the source\'s unit tests (446 cases, `runtime/tests/TransactHub.Activities.Wps.Tests/Source`) |')
lines += ['', 'Not yet ported (P2): the SSM structure checks inline in `WpsPlatformActivities.cs`, `AccTxnDecisionRules`, `RbPostingClassifier`. The DE file layout is in the `file-bulk` adapter (R-12-15).', '',
 '## Platform activities', '', '| Activity | Source file | Target layer | Target | Status |', '| --- | --- | --- | --- | --- |']
for n in sorted(plat):
    layer, target, status = classify(n)
    lines.append(f'| `{n}` | `{os.path.basename(plat[n])}` | {layer} | {target} | {status} |')
lines += ['', '## Bank activities', '', 'Each becomes a bank value, hook or port binding of the shared flow; none survives as a bank activity.', '', '| Bank | Activity | Becomes | Status |', '| --- | --- | --- | --- |']
for n in sorted(bank, key=lambda k: (bank[k][0], k)):
    layer, target, status = classify(n)
    lines.append(f'| {bank[n][0]} | `{n}` | {target or "bank configuration"} ({layer}) | {"P5" if status.startswith("P2") else status} |')
counts = {}
for n in plat:
    s = classify(n)[2].split(';')[0].split(',')[0]
    counts[s] = counts.get(s, 0) + 1
lines += ['', '## Totals', '', '| Status | Platform activities |', '| --- | --- |'] + [f'| {k} | {v} |' for k, v in sorted(counts.items(), key=lambda x: -x[1])]
open(out, 'w').write('\n'.join(lines) + '\n')
print(len(plat), len(bank), counts)
