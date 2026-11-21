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
    (r'ParseJsonDocument|SaveToSalaryFileUploadQueue', 'p (intake)', 'front door intake; the file kept as a message in the store', 'store: messages and accepted bulk (DurableClaimCheck); intake: P2'),
    (r'UpdateFileStatus', 'p (store)', 'a status fact on the bulk; the status is a projection (insert-only)', 'store: status facts and projection'),
    (r'(Validate|Check).*Filename|FilenameDate|ValidateSifFilename', 'uae.wps', 'uae.wps.validate.salary-file (CBX file-name rules 1–7)', 'rules ported, golden'),
    (r'ParseSif|ValidateSif|InitiateSifFileParsing|ParseSifControl|ValidateWpsSifFile|ParseSifRecords|ParseAndQueueSifFile', 'uae.wps', 'uae.wps.map.sif-in + uae.wps.validate.salary-file / salary-record', 'rules ported, golden; activity on the studio contract: P2'),
    (r'GenerateCbxSif|GenerateSifFile|GenerateAndTransferSif', 'uae.wps', 'uae.wps.map.sif-out (CentralBankSif from the ISO 20022 store)', 'ported, golden; generated from the store'),
    (r'SubmitSifToCb|UploadSifToCbSftp|SftpUpload|TransferSif', 'uae.p + uae.wps', 'uae.wps.exchange.send-salary-file over the uae.p CB SFTP port', 'studio factory activity runs on Temporal; source transport: P2'),
    (r'ProcessCbResponse|ReceiveSifAckNak|Ack|Nak', 'uae.wps', 'uae.wps.map.ack-in and the leg\'s NAK path', 'studio factory flow runs on Temporal; source parity: P2'),
    (r'Duplicate', 'p (pattern) + uae.wps (keys)', 'p.validate.duplicate with uae.wps keys', 'studio factory activity; source keys: P2'),
    (r'Approval|Quorum|PaymentQueue', 'p', 'p.hold.approval / operations queue', 'studio factory activity; source policy rules: P2'),
    (r'Charge', 'p + b values', 'p charges activity, slabs/VAT/narration as bank values', 'P2'),
    (r'BalanceEnquiry|CorePosting|ReverseCorePosting|PostingStatus|ReconcilePosting|PostingIndeterminate|RecordPostingStatus|ConstructSalaryAmountPosting|RetrieveBranchCode|CoreBanking|WpsCoreBankingPosting|ReverseWpsPosting|ReversalPosting', 'p via core-banking port; b binding', 'p.reserve.funds / p.post.accounting / p.post.reverse / p.post.verify over p.port.core-banking', 'studio factory activities run on Temporal; bank adapter binding: P5'),
    (r'Decrypt', 'p', 'p decrypt (file intake)', 'P2'),
    (r'Corporate|MolEstablishment|Precheck|UploadRequest|SaveUiUploaded|LocateAndReadSalaryFile|InitializeSifUpload|ExtractAndParseSifUpload|AuditQueue|QueueSif', 'p (intake) + uae.wps (MOL)', 'front door intake; uae.wps MOL corporate match (SifUploadRules.MatchCorporate)', 'MatchCorporate ported, golden; intake: P2'),
    (r'Pif|Psi', 'uae.wps', 'uae.wps.map.pif-in, inward leg', 'studio factory map exists; source parity: P2'),
    (r'Prc', 'uae.wps', 'uae.wps inward PRC flow', 'P2'),
    (r'Dif', 'uae.wps', 'uae.wps inward DIF flow', 'P2'),
    (r'Rfa', 'uae.wps', 'uae.wps RFA flow', 'P2'),
    (r'Rri', 'uae.wps; b port for block/release', 'uae.wps RRI flow', 'P2'),
    (r'DeBulk|DeOutcome|DeUpload|RepostFailedDe|LostDe|DetermineDe|SubmitDe|TransferDe', 'uae.wps; b port for upload', 'uae.wps DE bulk flow', 'P2'),
    (r'CbCallWindow|PostingWindow|CbPosting|Restricted|Rb[A-Z]', 'p scheduler; b values', 'schedule-gated await (posting windows)', 'P2'),
    (r'ExchangeRate', 'p', 'p exchange-rate refresh (not WPS)', 'P2'),
    (r'Hook', 'p hook points', 'workflow hook points with bank bindings', 'hooks run on Temporal (kit CK-HOOK-01..04)'),
    (r'Rtc', 'uae.wps', 'uae.wps RTC flow (ADIB posting as a hook)', 'P2'),
    (r'Mohre|Notify', 'p.notify + uae.wps', 'p.notify.channel; MOHRE notify as a bank hook', 'studio factory notify runs on Temporal; MOHRE: P5'),
    (r'Aml|Screen', 'p', 'p.screen.sanctions / p.screen.fraud', 'studio factory activities run on Temporal'),
    (r'Route', 'p', 'front door workflow-select', 'studio front door runs on Temporal'),
    (r'SftpDownload', 'p', 'file intake watcher', 'P2'),
]
def classify(n):
    for p, layer, target, status in RULES:
        if re.search(p, n): return layer, target, status
    return 'to decide', '', 'P2: not yet mapped'

lines = ['# WPS parity matrix', '',
 'Generated from the TransactHub source by `docs/runtime/parity.py`. Every activity the source registers for WPS, on the platform and in each bank, with where it goes in the north-star layout and what proves it today. "Golden" means the runtime\'s port answers a corpus generated from the source\'s own code exactly (`runtime/tests/TransactHub.Store.Tests/Golden`).', '',
 f'Platform WPS activities: {len(plat)}. Bank WPS activities: {len(bank)} (every one is removed: a bank is configuration, hooks and port bindings).', '',
 '## Rule classes', '', '| Source | Runtime | Proof |', '| --- | --- | --- |']
for c in ['CbxFileNameRules', 'CbxRecordRules', 'CbxScrRules', 'CbxRuleSettings', 'SifUploadRules (file name, date, corporate match)', 'CbxSifWriter', 'AccountMasking']:
    lines.append(f'| `{c}` | `TransactHub.Activities.Wps` | golden, 1,878 cases in all |')
lines += ['', 'Not yet ported (P2): the SSM structure checks inline in `WpsPlatformActivities.cs`, the DE, DIF, RFA, PRC and RRI rule classes, `WpsEmployeeBankClassifier`, `AccTxnDecisionRules`, `RbPostingClassifier`.', '',
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
