import { useEffect, useState } from 'react';
import type { Client } from './api';
import { messageChoices, uploadAnswer, type UploadAnswer, type UploadTarget } from './upload';
import { Button, Select } from '@fluentui/react-components';
import { Alert, Badge } from '../ui';

// The upload page (R-05-73, R-05-74, Issue I-201): a person puts a file in by hand. The tenant is the runtime's bank, the message
// one the active distribution receives; the file goes to the File API's POST /files/ui with the person's own sign-in, so the File
// API checks it as any upload. The answer says whether the file was kept, refused or started a payment, with the runbook of a
// refusal's code and a link to the payment it started.

export function Upload({ api, go }: { api: Client; go(h: string): void }) {
  const [offer, setOffer] = useState<{ fileApi: boolean; environment: string; targets: UploadTarget[] } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [tenant, setTenant] = useState('');
  const [message, setMessage] = useState('');
  const [file, setFile] = useState<File | null>(null);
  const [busy, setBusy] = useState(false);
  const [answer, setAnswer] = useState<UploadAnswer | null>(null);
  useEffect(() => {
    api.upload().then((o) => { setOffer(o); setTenant(o.targets[0]?.tenant ?? ''); }).catch((e) => setError(String(e.message ?? e)));
  }, []);
  const target = offer?.targets.find((t) => t.tenant === tenant);
  const choices = messageChoices(target);
  useEffect(() => { if (!choices.some((m) => m.id === message)) setMessage(choices[0]?.id ?? ''); }, [tenant, offer]);
  async function send() {
    if (!file) return;
    setBusy(true); setAnswer(null); setError(null);
    try { const r = await api.uploadFile(tenant, message, file); setAnswer(uploadAnswer(r.body, r.status)); }
    catch (e: any) { setError(String(e.message ?? e)); }
    setBusy(false);
  }
  return (
    <section>
      <h1>Upload a file</h1>
      <p className="note">The file goes to the File API as your upload: it checks it, keeps it, and starts its payment on the distribution active in {offer?.environment ?? 'this environment'}, as for any file that comes in.</p>
      {error && <Alert tone="fail" className="error" role="alert">{error}</Alert>}
      {offer && !offer.fileApi && <Alert tone="fail" className="error" role="alert">This console has no File API to post to (Operations:FileApi:Url).</Alert>}
      {target && !target.distribution && <p className="muted">No distribution of {target.tenant} is active in {target.environment}: nothing can be uploaded.</p>}
      <div className="filters">
        <label><span>Tenant</span>
          <Select name="tenant" aria-label="Tenant" value={tenant} onChange={(e) => setTenant(e.target.value)}>
            {offer?.targets.map((t) => <option key={t.tenant} value={t.tenant}>{t.tenant}</option>)}
          </Select>
        </label>
        <label><span>Message</span>
          <Select name="message" aria-label="Message" value={message} onChange={(e) => setMessage(e.target.value)}>
            {choices.map((m) => <option key={m.id} value={m.id}>{m.title} ({m.id}{m.kind === 'deliver' ? ', to a running payment' : ''})</option>)}
          </Select>
        </label>
        <label><span>File</span><input type="file" name="file" aria-label="File" onChange={(e) => setFile(e.target.files?.[0] ?? null)} /></label>
        <Button appearance="primary" disabled={!file || !message || !tenant || busy || !offer?.fileApi} onClick={send}>Upload</Button>
      </div>
      {target?.distribution && <p className="muted small">Active distribution: <span className="mono">{target.distribution}</span></p>}
      {answer && (
        <p role="status" data-testid="upload-answer">
          <Badge className={`badge ${answer.outcome === 'refused' ? 'fail' : answer.outcome === 'started' ? 'pass' : 'warn'}`}>{answer.outcome}</Badge>{' '}
          {answer.text}{' '}
          {answer.code && <a href={answer.runbook} data-testid="error-link">Runbook for {answer.code}</a>}
          {answer.payment && <> <a href={`#/payments/${encodeURIComponent(answer.payment)}`} onClick={(e) => { e.preventDefault(); go(`#/payments/${encodeURIComponent(answer.payment!)}`); }} data-testid="upload-payment">Payment {answer.payment}</a></>}
        </p>
      )}
    </section>
  );
}
