// Ask the labour ministry's establishment register for the employer's status (Issue I-88). The register answers active
// (accepted), suspended, or that it does not know the employer. The status is written to the scheme's extension, where
// the salary file checks read it (uae.wps.rule.employer-not-suspended.v1); an employer with no known status is not
// refused for it, as in the source (an empty mohreStatus passed its WPS rule step).

export async function run({ ctx, ports }) {
  const reply = await ports.request('uae.wps.port.labour-ministry.v1');
  const status = reply.status === 'suspended' ? 'suspended'
    : reply.status === 'accepted' || reply.status === 'active' ? 'active'
    : 'unknown';
  // Only the status is added: the activity declares that one write, and what it read stays as it was.
  const own = (ctx.ext && ctx.ext.uae && ctx.ext.uae.wps) || {};
  ctx.ext = { uae: { wps: { ...own, employerStatus: status } } };
  return status === 'unknown' ? 'not-found' : 'found';
}
