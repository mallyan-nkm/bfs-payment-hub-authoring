import { Fragment, type ReactNode } from 'react';

// A small, safe subset of markdown for the assistant's replies (I-30): paragraphs, `- ` and `1. `
// lists, `#` headings shown as a bold line, **bold** and `code`. It builds React elements and
// never sets HTML, so any tag in the text is shown as text. There are no links: a URL stays text.

/** Inline marks: `code` first, so its content stays literal, then **bold** in what is left. */
function inline(text: string, key: string): ReactNode[] {
  const out: ReactNode[] = [];
  const re = /`([^`\n]+)`|\*\*([^*\n]+?)\*\*/g;
  let at = 0;
  let m: RegExpExecArray | null;
  while ((m = re.exec(text))) {
    if (m.index > at) out.push(text.slice(at, m.index));
    out.push(m[1] !== undefined ? <code key={`${key}.${m.index}`}>{m[1]}</code> : <b key={`${key}.${m.index}`}>{m[2]}</b>);
    at = m.index + m[0].length;
  }
  if (at < text.length) out.push(text.slice(at));
  return out;
}

const BULLET = /^\s*[-*]\s+(.*)$/;
const NUMBER = /^\s*\d+[.)]\s+(.*)$/;
const HEADING = /^\s*#{1,6}\s+(.*)$/;

/** Render an assistant's reply. */
export function Markdown({ text }: { text: string }) {
  const blocks: ReactNode[] = [];
  const lines = text.replace(/\r\n?/g, '\n').split('\n');
  let para: string[] = [];
  let list: { ordered: boolean; items: string[] } | undefined;
  const flush = () => {
    const k = `b${blocks.length}`;
    if (para.length) blocks.push(<p key={k}>{para.map((l, i) => <Fragment key={i}>{i > 0 && <br />}{inline(l, `${k}.${i}`)}</Fragment>)}</p>);
    else if (list) {
      const items = list.items.map((it, i) => <li key={i}>{inline(it, `${k}.${i}`)}</li>);
      blocks.push(list.ordered ? <ol key={k}>{items}</ol> : <ul key={k}>{items}</ul>);
    }
    para = [];
    list = undefined;
  };
  for (const line of lines) {
    const b = BULLET.exec(line) ?? undefined;
    const n = b ? undefined : NUMBER.exec(line) ?? undefined;
    const h = HEADING.exec(line);
    if (!line.trim()) flush();
    else if (b || n) {
      const ordered = !!n;
      if (para.length || (list && list.ordered !== ordered)) flush();
      list ??= { ordered, items: [] };
      list.items.push((b ?? n)![1]);
    } else if (h) {
      flush();
      blocks.push(<p key={`b${blocks.length}`}><b>{inline(h[1].replace(/\*\*/g, ''), `h${blocks.length}`)}</b></p>);
    } else if (list && /^\s+\S/.test(line)) list.items[list.items.length - 1] += ` ${line.trim()}`;
    else {
      if (list) flush();
      para.push(line);
    }
  }
  flush();
  return <div className="md">{blocks}</div>;
}
