// The script-behind of b.notify.labour-ministry.v1: tell the labour ministry that a salary file closed (the
// source's MohreNotification). The ministry takes the notification; a refusal is a failure of the notification,
// which the notify hook point's fail-open policy keeps away from the payment, as the source did.

export async function run({ ctx, ports }) {
  const reply = await ports.request('b.port.labour-ministry.v1');
  if (reply.status === 'rejected') throw new Error(`The labour ministry refused the notification${reply.reasonCode ? ` (${reply.reasonCode})` : ''}`);
  ctx.facts = { notified: { outcome: 'notified', to: 'labour-ministry' } };
  return 'notified';
}
