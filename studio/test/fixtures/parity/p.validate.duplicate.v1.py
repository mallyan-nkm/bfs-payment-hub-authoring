# The duplicate check in Python. The parity suite runs it against the TypeScript script-behind
# in studio/scripts/ on the same fixtures; both must give the same outcome and writes.

def run(api):
    reply = api.ports.request('p.port.payment-record.v1')
    found = reply.get('status') in ('rejected', 'found')
    api.ctx['facts'] = {'duplicate': {'found': found}}
    return 'duplicate' if found else 'unique'
