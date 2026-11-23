// Look the debtor up in the bank's party register (R-12-27, Issue I-149). The register is bank customer data and lives in the
// runtime only: in the studio a simulator answers for it, and on the runtime the native code-behind reads the register as of the
// step, by the debtor's organisation id (Dbtr/Id/OrgId/Othr) matched against the field `lookupBy` names. The answer is registered
// (found and active), not registered (not found, or inactive), or a mismatch (found, with other details than the ones supplied).
// Only the answer is written, never the register's record: no name, account or contact reaches the workflow's state.

export async function run({ ctx, ports, params }) {
  const reply = await ports.request('p.port.party-register.v1');
  const status = String(reply.status ?? '');
  const found = status !== 'not-found';
  const active = found && status !== 'inactive';
  const register = params.register ?? 'corporate';
  const lookupBy = params.lookupBy ?? 'establishmentId';
  const outcome = !found || !active ? 'not-registered' : status === 'mismatch' ? 'mismatch' : 'registered';
  ctx.facts = { ...(ctx.facts ?? {}), partyRegister: { register, lookupBy, found, active, ...(outcome === 'mismatch' ? { mismatch: 'details' } : {}) } };
  if (outcome === 'not-registered') return { outcome, reasonCode: 'RR04' };
  if (outcome === 'mismatch') return { outcome, reasonCode: 'BE01' };
  return outcome;
}
