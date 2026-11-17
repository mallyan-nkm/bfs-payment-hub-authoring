// Templates for new artefacts (F-12): blank, or from a template with a worked shape.

export function templates(kind: string, wfKind: string | undefined, name: string | undefined, title: string | undefined, full: boolean): any {
  const release = '1.0.0';
  switch (kind) {
    case 'workflow': {
      const k = wfKind ?? 'leg';
      const base: any = { kind: 'workflow', wfKind: k, name: name ?? (k === 'platform' ? 'p.wf.new.v1' : k === 'segment' ? 'p.seg.new.v1' : k === 'handler' ? 'uae.aani.handler.new.v1' : 'uae.aani.wf.new.v1'), release, dsl: '1.0.0', title: title ?? 'New workflow' };
      if (k === 'platform') Object.assign(base, {
        direction: 'outward',
        entry: { reads: ['header', 'payment'] },
        exit: { writes: ['status'], outcomes: ['settled', 'rejected', 'review'] },
        slots: [{ name: 'scheme-leg', contract: { reads: ['header', 'payment'], writes: ['status', 'messages'], outcomes: ['settled', 'rejected', 'pending'], primaryOutcome: 'settled' } }],
        hookPoints: full ? [{ name: 'before-release', type: 'halt', description: 'A bank may hold or reject a payment before it leaves.', contract: { reads: ['header', 'payment'] }, policy: { timeout: 'PT1S', onFailure: 'fail-closed', onHold: 'p.hold.approval.v1' } }] : [],
        signals: ['pause', 'resume', 'cancel', 're-screen'],
        do: full
          ? [
              { id: 'validate', call: 'p.validate.canonical.v1', handle: { repair: { end: 'rejected' }, reject: { end: 'rejected' } } },
              { hook: 'before-release' },
              { id: 'leg', slot: 'scheme-leg', handle: { rejected: { end: 'rejected' }, pending: { end: 'review' } } },
              { end: 'settled' },
            ]
          : [{ end: 'settled' }],
        finally: full ? [{ id: 'notify', call: 'p.notify.channel.v1' }] : undefined,
      });
      if (k === 'leg') Object.assign(base, {
        direction: 'outward',
        implements: { slot: 'scheme-leg', of: ['p.wf.commercial.v1'] },
        entry: { reads: ['header', 'payment'] },
        exit: { writes: ['status', 'messages'], outcomes: ['settled', 'rejected', 'pending'] },
        do: full
          ? [
              { id: 'scheme-checks', call: 'uae.aani.validate.credit.v1', handle: { reject: { end: 'rejected' } } },
              { id: 'render', call: 'uae.aani.map.credit-out.v1', handle: { unmappable: { end: 'rejected' } } },
              { end: 'settled' },
            ]
          : [{ end: 'settled' }],
      });
      if (k === 'segment') Object.assign(base, { do: [{ id: 'validate', call: 'p.validate.canonical.v1', handle: { repair: { end: 'rejected' }, reject: { end: 'rejected' } } }] });
      if (k === 'handler') Object.assign(base, { direction: 'inward', exit: { outcomes: ['answered'] }, do: [{ id: 'find', call: 'p.enrich.payment-record.v1', handle: { 'not-found': { continue: true } } }, { end: 'answered' }] });
      if (!base.finally) delete base.finally;
      base.provenance = { origin: 'factory', status: 'provisional' };
      return base;
    }
    case 'activity':
      return {
        kind: 'activity', name: name ?? 'uae.aani.validate.new.v1', release, title: title ?? 'New activity', type: 'validate',
        reads: ['header', 'payment'], writes: ['facts.validation'],
        outcomes: [{ name: 'pass', primary: true }, { name: 'reject' }], effect: 'pure', ruleSets: ['uae.aani.ruleset.credit.v1'],
        implementation: { binding: 'builtin', service: 'rule-service' }, provenance: { origin: 'factory', status: 'provisional' },
      };
    case 'rule':
      return {
        kind: 'rule', name: name ?? 'uae.aani.rule.new.v1', release, title: title ?? 'New rule', ruleKind: 'field', repeatability: 'non-repeatable',
        when: [{ match: { fact: 'Payment', as: 'p', where: "isBlank(p.purpose)" } }],
        then: [{ yield: { finding: { severity: 'repair', path: 'payment.purpose', reasonCode: 'NARR' } } }],
        examples: [
          { name: 'passes', evaluationDate: '2026-11-17', given: { Payment: { purpose: 'SALA' } }, expect: { findings: [] } },
          { name: 'fails', evaluationDate: '2026-11-17', given: { Payment: {} }, expect: { findings: [{ reasonCode: 'NARR' }] } },
        ],
        provenance: { origin: 'factory', status: 'provisional' },
      };
    case 'hook':
      return {
        kind: 'hook', name: name ?? 'b.hook.new.v1', release, title: title ?? 'New hook',
        implements: { hookPoint: 'before-release', of: 'p.wf.corporate.v1' }, type: 'halt', outcomes: ['continue', 'hold', 'reject'],
        do: [{ id: 'internal-limit', call: 'b.validate.internal-limit.v1', handle: { reject: { end: 'hold' } } }, { end: 'continue' }],
        provenance: { origin: 'authored', status: 'provisional' },
      };
    default:
      return { kind, name: name ?? '', release, title: title ?? '' };
  }
}
