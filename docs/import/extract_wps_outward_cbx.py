"""Extract the TransactHub source's WPS outward flow into an import bundle (docs/import/EXTRACTION-PROMPT.md).

The slice of the north-star plan's step 2: the source's outward channel flow (dsl/workflows/dib-wps-outward.v1)
and the rule classes it runs, as studio content. Every step of the flow and every class is inventoried: two
become native activities whose code is the runtime's port of the source (golden-tested), the rest map to the
platform lifecycle the studio already has, or are named as not yet done with the reason.

Usage: python3 -I extract_wps_outward_cbx.py <unpacked TransactHub-src> <dsl dir> <out dir>
The bundle carries no bank's data: the values are synthetic and the bank is the example bank.
"""
import copy, hashlib, json, os, sys
import yaml

src, dsl, out = sys.argv[1], sys.argv[2], sys.argv[3]
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
FLOW = 'dsl/workflows/dib-wps-outward.v1.workflow.json'
RULES = 'services/shared/dotnet/PaymentHub.Shared/Domain/Wps'
CLASSES = {
    f'{RULES}/CbxFileNameRules.cs': ('CbxFileNameRules', None, 'File-name rules 1–3 and timestamp rules 5–7. To become studio rules of the CBX intake (R-03-08: validation is declarative), proven against the runtime\'s port with the golden corpus (I-36).'),
    f'{RULES}/CbxRecordRules.cs': ('CbxRecordRules', None, 'Employee record rules 4/5, 7, 14, 32, 36, 46/47, 54–60, 77, 79. To become studio rules (I-36); EVP and rule 79 need the raw file lines, which the canonical context does not keep.'),
    f'{RULES}/CbxScrRules.cs': ('CbxScrRules', None, "Control record rules 66 and 81–83. To become studio rules (I-36); 82/83 need the corporate's registered accounts, a lookup the platform does not have yet."),
    f'{RULES}/CbxRuleSettings.cs': ('CbxRuleSettings', None, "The bank's values for those rules: parameters of the studio rules once they exist (I-36)."),
    'services/activities/platform/PaymentHub.Activities.Platform.Wps/CbxSifWriter.cs': ('CbxSifWriter', 'uae.wps.map.cb-sif-out.v1', 'The Central Bank SIF writer'),
}
PROV = lambda note: {'origin': 'authored', 'status': 'provisional', 'clauses': ['TransactHub source 666f1ee', 'UAEWPS SSM V2020-001'], 'note': note}

def dump(path, doc):
    p = os.path.join(out, path)
    os.makedirs(os.path.dirname(p), exist_ok=True)
    with open(p, 'w') as f:
        yaml.safe_dump(doc, f, sort_keys=False, allow_unicode=True)

def params(*names):
    all_ = {
        'CbxMolIdLength': {'type': 'integer', 'default': 13, 'bounds': {'min': 13, 'max': 35}, 'description': 'Length of the MOL establishment id at the start of a CBX file name.'},
        'CbxFileDateMaxYearsBack': {'type': 'integer', 'default': 1, 'bounds': {'min': 0, 'max': 99}, 'description': 'How many years back a file-name timestamp may fall.'},
        'CbxScrDebitAccountRequired': {'type': 'boolean', 'default': True, 'description': 'Rule 82: the control record carries the debit account.'},
        'CbxScrDebitAccountRegisteredCheck': {'type': 'boolean', 'default': True, 'description': "Rule 83: the debit account is one of the corporate's registered accounts."},
        'CbxAccountMatchLength': {'type': 'integer', 'default': 15, 'bounds': {'min': 1, 'max': 34}, 'description': 'Rule 83 compares this many characters from the right.'},
        'BankRoutingCode': {'type': 'code', 'default': '000000000', 'description': "This bank's own routing code (rule 66). The default matches no bank, so a bank that has not set it refuses every file."},
        'FreezoneId': {'type': 'code', 'default': '000', 'description': "The corporate's freezone id; 000 is mainland (rules 4/5)."},
    }
    return {n: all_[n] for n in names}

shutil_rm = lambda d: os.path.isdir(d) and __import__('shutil').rmtree(d)
shutil_rm(out)
os.makedirs(out)

cbx = {
    'kind': 'activity', 'name': 'uae.wps.validate.cbx-file.v1', 'release': '1.0.0', 'title': 'CBX salary file checks',
    'description': "The source's CBX channel checks on a salary file, on the canonical context: the file name and its timestamp, each employee record, a duplicate agent and account, and the control record's bank routing code. Codes and messages are the source's. For files from the CBX channel.",
    'type': 'validate', 'level': 'batch',
    'reads': ['header', 'batch', 'items[]', 'ext.uae.wps'], 'writes': ['facts.validation'],
    'outcomes': [{'name': 'pass', 'primary': True}, {'name': 'reject'}], 'effect': 'pure',
    'parameters': params('CbxMolIdLength', 'CbxFileDateMaxYearsBack', 'CbxScrDebitAccountRequired', 'CbxScrDebitAccountRegisteredCheck', 'CbxAccountMatchLength', 'BankRoutingCode', 'FreezoneId'),
    'implementation': {'binding': 'native'},
    'provenance': PROV('Extracted from the source; its code is the runtime\'s port (runtime/src/TransactHub.Activities.Wps), golden-tested against the source.'),
}
render = {
    'kind': 'activity', 'name': 'uae.wps.map.cb-sif-out.v1', 'release': '1.0.0', 'title': 'Canonical batch to the Central Bank SIF',
    'description': "The source's Central Bank SIF writer (CbxSifWriter), from the canonical batch: each EDR, then the SCR. The file is kept by the native host; the context keeps its name, hash and totals.",
    'type': 'map', 'reads': ['header', 'batch', 'items[]', 'ext.uae.wps'], 'writes': ['messages'],
    'outcomes': [{'name': 'mapped', 'primary': True}, {'name': 'unmappable'}], 'effect': 'pure',
    'map': {'direction': 'out', 'sourceModel': 'WPS SIF as the Central Bank receives it: EDR, EVP and SCR records', 'roundTrip': False},
    'parameters': {'bankRoutingCode': params('BankRoutingCode')['BankRoutingCode']},
    'implementation': {'binding': 'native'},
    'provenance': PROV("Extracted from the source's CbxSifWriter; its code is the runtime's port, golden-tested byte for byte."),
}
dump('artefacts/activities/uae.wps.map.cb-sif-out.v1.yaml', render)

# The leg, release 1.1.0: the same interface, the file rendered by the source's writer.
leg = yaml.safe_load(open(os.path.join(REPO, 'spec/factory/uae.wps/workflows/uae.wps.wf.salary-file-out.v1.yaml')))
leg['release'] = '1.1.0'
for st in leg['do']:
    if st.get('id') == 'render':
        st['call'] = 'uae.wps.map.cb-sif-out.v1'
leg['provenance'] = PROV('Release 1.1.0 of the factory leg: the salary file is rendered by the source\'s Central Bank SIF writer (native). Same interface: payments started on 1.0.0 finish on it.')
dump('artefacts/workflows/uae.wps.wf.salary-file-out.v1.yaml', leg)

# Tests: the CBX checks on a canonical context, and the leg with the native writer.
ctx = {
    'header': {'scheme': 'uae.wps', 'direction': 'outward', 'fileId': '0000001234567261117083000', 'endToEndId': '0000001234567261117083000'},
    'batch': {'id': '0000001234567261117083000', 'currency': 'AED', 'controlSum': 1500, 'numberOfTransactions': 1, 'categoryPurpose': 'SALA', 'debtorAgent': {'clearingMemberId': '302620122'}},
    'items': [{'payment': {'creditorAgent': {'clearingMemberId': '203320101'}, 'creditorAccount': {'iban': 'AE070331234567890123456'}, 'instructedAmount': {'value': 1500, 'currency': 'AED'}},
               'ext': {'uae': {'wps': {'personId': '00001234567890', 'payStartDate': '2026-10-01', 'payEndDate': '2026-10-31', 'daysInPeriod': 31, 'fixedPay': 1200, 'variablePay': 300, 'leaveDays': 0}}}}],
    'ext': {'uae': {'wps': {'employerId': '0000001234567', 'salaryMonth': '102026', 'employerReference': 'REF-0001', 'fileCreationDate': '2026-11-17', 'fileCreationTime': '0830'}}},
}
def t(id_, title, target, level, outcome, given, **extra):
    d = {'kind': 'test', 'id': id_, 'title': title, 'target': target, 'level': level, 'evaluationDate': '2026-11-17', 'given': given, 'expect': {'outcome': outcome}}
    d.update(extra)
    dump(f'tests/{id_}.yaml', d)
t('WPS-CBX-006', 'The leg renders the file with the source\'s writer and settles', 'uae.wps.wf.salary-file-out.v1', 'leg', 'settled', {'inbound': 'samples/wps-sif-valid.sif'},
  bench=[{'port': 'uae.wps.port.wps.v1', 'simulator': 'uae.wps.sim.wps-outward.v1', 'scenario': 'accept'}, {'port': 'uae.p.port.calendar.v1', 'simulator': 'uae.p.sim.calendar.v1', 'scenario': 'accept'}])
with open(os.path.join(out, 'tests/WPS-CBX-006.yaml')) as f: d6 = yaml.safe_load(f)
d6['expect']['trace'] = ['business-date:set', 'file-checks:pass', 'record-checks:pass', 'record-iban:pass', 'render:mapped', 'submit:accepted', 'end:settled']
dump('tests/WPS-CBX-006.yaml', d6)

# The example bank's values for the new parameters.
cs = yaml.safe_load(open(os.path.join(REPO, 'spec/examples/bank/configuration-set.yaml')))
# A later release than the example bank's own, which it carries forward (R-11-35: the earlier stays as published).
maj, mi, _ = (int(x) for x in cs['release'].split('.'))
cs['release'] = f'{maj}.{mi + 1}.0'
cs['title'] = f"{cs.get('title', 'Configuration')}, with its routing code for the source's CBX checks"
cs.setdefault('values', []).append({'parameter': f"{render['name']}#bankRoutingCode", 'value': '302620122', 'validFrom': '2026-11-01'})
dump(f"configuration/{cs['tenant']}.yaml", cs)

# The inventory: every step of the source flow and every class.
flow = json.load(open(os.path.join(dsl, 'workflows/dib-wps-outward.v1.workflow.json')))
STEP = {
    'decrypt_file': ('unmapped', None, 'GnuPG decryption at intake: a p.* intake activity (plan P2). The studio\'s front door takes the file already decrypted.'),
    'validate_sif': ('artefact', 'uae.wps.validate.salary-file.v1', 'The factory file checks run here; the source\'s CBX and SSM checks are to become studio rules (I-36).'),
    'evaluate_approval_policy': ('artefact', 'p.hold.approval.v1', 'Approval is the platform hold; its policy rules are the bank\'s (P5).'),
    'check_approval_required': ('artefact', 'p.wf.corporate.v1', 'A branch of the platform lifecycle.'),
    'enter_payment_queue': ('artefact', 'p.hold.approval.v1', 'The operations queue behind the hold.'),
    'await_approval': ('artefact', 'p.hold.approval.v1', 'The hold waits for the decision.'),
    'check_approval_outcome': ('artefact', 'p.wf.corporate.v1', 'A branch of the platform lifecycle.'),
    'approval_not_approved': ('artefact', 'p.wf.corporate.v1', 'The lifecycle ends rejected and undoes the reservation.'),
    'calculate_charges': ('unmapped', None, 'Charges are a p.* activity with the bank\'s slabs, VAT and narration as values (P2, P5).'),
    'balance_enquiry': ('artefact', 'p.reserve.funds.v1', 'The source enquires and later posts; the lifecycle reserves funds (review issue 3).'),
    'check_balance': ('artefact', 'p.wf.corporate.v1', 'A branch of the platform lifecycle.'),
    'check_balance_proceeded': ('artefact', 'p.wf.corporate.v1', 'A branch of the platform lifecycle.'),
    'core_posting': ('artefact', 'p.post.accounting.v1', 'Posting through the core-banking port; the bank\'s adapter is a port binding (P5).'),
    'check_posted': ('artefact', 'p.wf.corporate.v1', 'A branch of the platform lifecycle.'),
    'check_posting_unknown': ('artefact', 'p.post.verify.v1', 'An unknown posting is verified, never guessed.'),
    'reconcile_posting': ('artefact', 'p.post.verify.v1', 'Verification of the posting.'),
    'check_reconciled': ('artefact', 'p.wf.corporate.v1', 'A branch of the platform lifecycle.'),
    'end_reconciliation_required': ('artefact', 'p.hold.repair.v1', 'An unreconciled posting goes to repair.'),
    'submit_to_cb': ('artefact', 'uae.wps.map.cb-sif-out.v1', 'The SIF is rendered by the source\'s writer; it is sent by the factory composite uae.wps.exchange.submit-salary-file.v1.'),
    'project_cb_message': ('infrastructure', None, 'A projection for screens: in the runtime, facts in the store and their projection.'),
    'await_cb_response': ('artefact', 'uae.wps.exchange.submit-salary-file.v1', 'The composite sends, waits for the acknowledgement and reads it.'),
    'check_cb_response': ('artefact', 'uae.wps.wf.salary-file-out.v1', 'The leg handles the acknowledgement\'s outcomes.'),
    'notify_rejection': ('artefact', 'p.notify.channel.v1', 'The channel is told.'),
    'reverse_posting': ('artefact', 'p.post.reverse.v1', 'Compensation of the posting.'),
    'notify_acceptance': ('artefact', 'p.notify.channel.v1', 'The channel is told.'),
}
items = []
def item(path, symbol, kind, summary, target, name, note):
    tgt = {'kind': target} if name is None else {'kind': target, 'name': name}
    it = {'id': f'SRC-{len(items) + 1:04d}', 'source': {'path': path, 'symbol': symbol, 'kind': kind}, 'summary': summary, 'target': tgt, 'note': note, 'confidence': 'high'}
    if name and (name.startswith('p.') or name.startswith('uae.p.') or name in ('uae.wps.exchange.submit-salary-file.v1',)): it['reuse'] = 'as-is'
    items.append(it)
for st in flow['steps']:
    target, name, note = STEP[st['id']]
    kind = 'workflow' if st.get('activity_name') in (None, 'NoOp') else 'activity'
    item(FLOW, st['id'], kind, f"{st['id']}: {st.get('activity_name')}", target, name, note)
for path, (cls, name, note) in CLASSES.items():
    item(path, cls, 'rule' if 'Rules' in cls or 'Settings' in cls else 'map', cls, 'artefact' if name else 'unmapped', name, note)
item(FLOW, 'WPS-CBX-006', 'test', 'The flow renders and submits the file, as test WPS-CBX-006', 'test', 'WPS-CBX-006', 'The source\'s writer in the leg; the same files are in the golden corpus.')
item('spec/examples/bank/configuration-set.yaml', 'values', 'config', "The example bank's routing code for the new parameters", 'configuration', f"{cs['tenant']}@{cs['release']}", 'Synthetic: the bank\'s real values stay in its own repository (D5).')
dump('inventory.yaml', {'items': items})

files = [FLOW] + list(CLASSES) + ['spec/examples/bank/configuration-set.yaml']
dump('extraction.yaml', {
    'kind': 'extraction', 'format': 1, 'scope': 'uae.wps',
    'title': "WPS outward: the source's Central Bank SIF writer (slice 1)",
    'source': {'system': 'TransactHub WPS (source)', 'repository': 'TransactHub-src.zip', 'revision': '666f1ee', 'engine': 'PaymentHub DSL 3 on Temporal and DTFx',
               'languages': ['csharp', 'json'], 'files': files, 'excluded': []},
    'extractedBy': {'tool': 'docs/import/extract_wps_outward_cbx.py', 'model': 'none (a generator, reviewed by the designer)', 'date': '2026-11-21', 'promptRef': 'docs/import/EXTRACTION-PROMPT.md'},
    'dataPolicy': 'synthetic',
})
dump('findings.yaml', {'findings': [
    {'kind': 'decision', 'text': 'The CBX checks apply only to files from the CBX channel (the source says so), and validation is declarative in the studio (R-03-08). They become studio rules of the CBX intake, proven against the runtime\'s port of the source with the golden corpus (I-36); the leg keeps the factory checks.', 'source': f'{RULES}/CbxRecordRules.cs'},
    {'kind': 'question', 'text': 'Rules 79 (empty line) and the EVP rules need the raw file, which the canonical context does not keep. Run them at intake, before the map, or keep the raw lines as a message the activity reads?', 'source': f'{RULES}/CbxRecordRules.cs'},
    {'kind': 'question', 'text': "Rules 82/83 need the corporate's registered accounts: a lookup port the platform does not have yet.", 'source': f'{RULES}/CbxScrRules.cs'},
    {'kind': 'error-scenario', 'text': "A bank that has not set bankRoutingCode gets 000000000 in the control record of the file it sends: the Central Bank refuses it, as the source's fail-closed settings did.", 'source': f'{RULES}/CbxRuleSettings.cs'},
]})
open(os.path.join(out, 'README.md'), 'w').write("# WPS outward, slice 1\n\nGenerated by `docs/import/extract_wps_outward_cbx.py` from the TransactHub source. Import it from the studio's Import page, or `npm run studio -- import <this folder>`. Its native activities need the runtime's native host (`TH_NATIVE_HOST`).\n")
print(f'{len(items)} inventory items, {len(files)} source files -> {out}')
