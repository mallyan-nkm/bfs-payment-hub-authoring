// The canonical model as data (Part 2; C03-S1, C03-S2). ISO 20022 element names and data types for
// header, payment, batch, items, status and messages; `facts` registered per block from the paths
// blocks write; `ext.<scope>` from registered extensions. Every element says whether it is
// personal data (R-02-09).

export interface Element {
  path: string;
  name: string;
  isoType: string;
  personalData: boolean;
  description?: string;
  origin: 'iso' | 'extension' | 'fact' | 'reference';
  extension?: string;
  level?: 'batch' | 'item';
  children?: Element[];
}

type Spec = [string, string, boolean, string?] | [string, Record<string, any>, boolean?, string?];

const PARTY = {
  name: ['Max140Text', true, 'Name of the party'],
  postalAddress: {
    streetName: ['Max70Text', true], buildingNumber: ['Max16Text', true], postCode: ['Max16Text', true],
    townName: ['Max35Text', true, 'Town name, in its own element (R-02-13)'], countrySubDivision: ['Max35Text', true], country: ['CountryCode', false],
  },
  // Pty/Id/OrgId/Othr (R-02-21): an organisation's id and the scheme that issued it, for example an establishment id (MOL).
  // An organisation's id, not a person's: the party register is asked by it (R-12-27).
  organisationId: { id: ['Max35Text', false, 'The organisation\'s id (OrgId/Othr/Id)'], scheme: ['Max35Text', false, 'The scheme that issued the id (OrgId/Othr/SchmeNm/Prtry)'] },
  // R-02-22 (Issue I-153): the organisation's further ids, each an OrgId/Othr of its own, for example its CIF at the bank (scheme CIF).
  'otherOrganisationIds[]': { id: ['Max35Text', false, 'A further id of the organisation (OrgId/Othr/Id)'], scheme: ['Max35Text', false, 'The scheme that issued it (OrgId/Othr/SchmeNm/Prtry), CIF for the bank\'s customer number'] },
};
const ACCOUNT = {
  iban: ['IBAN2007Identifier', true, 'International bank account number'], other: ['Max34Text', true],
  // CashAccount40/Tp (Issue I-100): an ExternalCashAccountType1Code, or the scheme's own code as Prtry.
  type: ['CashAccountType2Choice', false, 'The kind of account: an ISO cash account type code, or the scheme\'s own'],
  proxy: { type: ['ExternalProxyAccountType1Code', false], value: ['Max2048Text', true] },
  // CashAccount40/Ccy (R-12-20): the account's currency, where the intake or core banking knows it; an order converts into it.
  currency: ['ActiveOrHistoricCurrencyCode', false, 'The currency of the account (CashAccount40/Ccy)'],
};
// FinInstnId/Nm (Issue I-100): the agent's name, looked up by its routing code.
const AGENT = { bicfi: ['BICFIDec2014Identifier', false], clearingMemberId: ['Max35Text', false, 'Clearing system member id, the routing code'], name: ['Max140Text', false, 'The agent\'s name, from the participant directory by its routing code'] };
// GrpHdr/InitgPty (Issue I-100): who initiated the file at the channel, by id only, never a name.
const INITIATING = { id: ['Max35Text', true, 'The channel user who uploaded or initiated the file, by id only (GrpHdr/InitgPty/Id)'] };
const AMOUNT = { value: ['ActiveCurrencyAndAmount', false, 'Amount'], currency: ['ActiveCurrencyCode', false] };

const TREE: Record<string, any> = {
  header: {
    paymentId: ['Max35Text', false, 'Platform id, assigned at intake. The root of every idempotency key.'],
    endToEndId: ['Max35Text', false, 'End-to-end identification from the originator'],
    uetr: ['UUIDv4Identifier', false], fileId: ['Max35Text', false, 'File or batch id; the correlation key for file rails'],
    kind: ['Code', false, 'payment, request, return or refund'], direction: ['Code', false, 'inward or outward'],
    // camt.056 Undrlyg/TxInf/OrgnlTxId (R-12-19): a refund order is a new order that refers to the earlier payment by its id.
    originalPaymentId: ['Max35Text', false, 'The id of the earlier payment a refund order refers to (camt.056 OrgnlTxId)'],
    segment: ['Code', false, 'corporate or commercial'], scheme: ['Code', false, 'The scope of the scheme'],
    channel: ['Code', false], receivedAt: ['ISODateTime', false], businessDate: ['ISODate', false],
    tenant: ['Max35Text', false, 'The bank tenant. For lookup, never for branching (R-02-16)'],
  },
  payment: {
    debtor: PARTY, debtorAccount: ACCOUNT, debtorAgent: AGENT, creditor: PARTY, creditorAccount: ACCOUNT, creditorAgent: AGENT,
    instructedAmount: AMOUNT, requestedExecutionDate: ['ISODate', false], purpose: ['ExternalPurpose1Code', false],
    categoryPurpose: ['ExternalCategoryPurpose1Code', false], remittanceInformation: { unstructured: ['Max140Text', true] },
    chargeBearer: ['ChargeBearerType1Code', false],
  },
  batch: {
    id: ['Max35Text', false], numberOfTransactions: ['Max15NumericText', false], controlSum: ['DecimalNumber', false], currency: ['ActiveCurrencyCode', false],
    categoryPurpose: ['ExternalCategoryPurpose1Code', false], debtorAgent: AGENT, creditorAgent: AGENT,
    // PmtInf/Dbtr and DbtrAcct, and GrpHdr/InitgPty (Issue I-100): carried from the channel's upload at intake.
    debtor: PARTY, debtorAccount: ACCOUNT, initiatingParty: INITIATING,
  },
  status: {
    code: ['ExternalPaymentTransactionStatus1Code', false, 'ISO payment status'], reasonCode: ['ExternalStatusReason1Code', false, 'ISO status reason'],
    schemeCode: ['Max35Text', false, 'A scheme code beside the ISO code (R-02-14)'], history: ['StatusHistory', false],
    // pain.002 TxInfAndSts (Issue I-100): one entry per line the scheme reported on, each with lineNo, code, reasonCode,
    // schemeCode and additionalInformation.
    transactions: ['TransactionStatusList', false, 'The status of each line the scheme reported on (pain.002 TxInfAndSts)'],
  },
  messages: {
    inbound: ['MessageReference', false, 'Reference to the raw inbound message (claim-check)'], received: ['MessageReferences', false],
    lastReceived: ['MessageReference', false], sent: ['MessageReferences', false], rendered: ['MessageReference', false],
  },
};

function build(prefix: string, node: any, origin: Element['origin']): Element[] {
  return Object.entries<any>(node).map(([k, v]) => {
    const path = prefix ? `${prefix}.${k}` : k;
    if (Array.isArray(v)) return { path, name: k, isoType: v[0], personalData: !!v[1], description: v[2], origin };
    const children = build(path, v, origin);
    return { path, name: k, isoType: 'Component', personalData: children.some((c) => c.personalData), origin, children };
  });
}

export class CanonicalModel {
  readonly roots: Element[];
  private index = new Map<string, Element>();

  constructor(extensions: any[], activities: any[]) {
    const iso = build('', TREE, 'iso');
    const items: Element = { path: 'items[]', name: 'items[]', isoType: 'TransactionList', personalData: true, origin: 'iso', children: [] };
    // An item carries its own payment (Part 2, 2.2).
    items.children = build('items[]', { payment: TREE.payment }, 'iso');
    const facts: Element = { path: 'facts', name: 'facts', isoType: 'BlockFacts', personalData: false, origin: 'fact', description: 'Block outputs that are not payment content; registered per block', children: [] };
    const factPaths = new Map<string, string[]>();
    for (const a of activities) for (const w of a.writes ?? []) if (w.startsWith('facts.')) {
      if (!factPaths.has(w)) factPaths.set(w, []);
      factPaths.get(w)!.push(a.name);
    }
    for (const [p, by] of [...factPaths].sort()) this.addUnder(facts, p, { isoType: 'BlockFact', personalData: false, origin: 'fact', description: `Written by ${by.join(', ')}` });
    const ext: Element = { path: 'ext', name: 'ext', isoType: 'SupplementaryData', personalData: false, origin: 'extension', children: [] };
    for (const e of extensions) {
      for (const f of e.fields ?? []) {
        const base = f.level === 'item' ? `items[].${e.root}` : e.root;
        const target = f.level === 'item' ? items : ext;
        this.addUnder(target, `${base}.${f.path}`, { isoType: f.isoType, personalData: !!f.personalData, origin: 'extension', description: f.description, extension: e.name, level: f.level });
        if (f.level === 'item') this.addUnder(ext, `${e.root}.${f.path}`, { isoType: f.isoType, personalData: !!f.personalData, origin: 'extension', description: `${f.description} (item level)`, extension: e.name, level: 'item' });
      }
      if (!(e.fields ?? []).length) this.addUnder(ext, e.root, { isoType: 'SupplementaryData', personalData: false, origin: 'extension', extension: e.name });
    }
    this.roots = [...iso.filter((r) => r.name !== 'messages'), items, facts, ext, iso.find((r) => r.name === 'messages')!];
    const visit = (e: Element) => {
      this.index.set(e.path, e);
      e.children?.forEach(visit);
    };
    this.roots.forEach(visit);
  }

  private addUnder(root: Element, path: string, attrs: Omit<Element, 'path' | 'name'>) {
    const rel = path.slice(root.path.length + 1).split('.');
    let cur = root;
    let p = root.path;
    rel.forEach((seg, i) => {
      p = `${p}.${seg}`;
      cur.children ??= [];
      let next = cur.children.find((c) => c.name === seg);
      if (!next) {
        next = i === rel.length - 1 ? { path: p, name: seg, ...attrs } : { path: p, name: seg, isoType: 'Component', personalData: false, origin: attrs.origin, extension: attrs.extension };
        cur.children.push(next);
      }
      if (i === rel.length - 1 && attrs.personalData) next.personalData = true;
      cur = next;
    });
  }

  /** Resolve a context path to its element (C03-S1). */
  resolve(path: string): Element | undefined {
    return this.index.get(path.replace(/\[\d+\]/g, '[]'));
  }

  all(): Element[] {
    return [...this.index.values()];
  }

  /** Paths whose values are personal data and must not appear in state, logs or traces (R-02-09). */
  personalPaths(): string[] {
    return this.all().filter((e) => e.personalData && !e.children?.length).map((e) => e.path);
  }

  search(q: string): Element[] {
    const s = q.toLowerCase();
    return this.all().filter((e) => e.path.toLowerCase().includes(s) || e.isoType.toLowerCase().includes(s) || (e.description ?? '').toLowerCase().includes(s));
  }
}

export function modelOf(store: { byKind(k: string): { doc: any }[] }): CanonicalModel {
  return new CanonicalModel(store.byKind('extension').map((e) => e.doc), store.byKind('activity').map((e) => e.doc));
}
