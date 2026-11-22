// The script-behind of b.screen.employer-aml.v1: ask the bank's AML service about the employer of a salary file
// (the source's DibWpsEmployerAmlCheck). The service refuses an employer it screens as a hit; that is the
// activity's hit outcome, with the service's reason code. Every other answer clears the employer. A service out of
// reach is a transient failure the activity's retry and the hook point's fail-closed policy deal with.

export async function run({ ctx, ports }) {
  const reply = await ports.request('b.port.aml-screening.v1');
  const hit = reply.status === 'rejected' || reply.status === 'hit';
  ctx.facts = { screening: { employer: { outcome: hit ? 'hit' : 'clear', ...(hit && reply.reasonCode ? { reasonCode: reply.reasonCode } : {}) } } };
  return hit ? { outcome: 'hit', reasonCode: reply.reasonCode } : 'clear';
}
