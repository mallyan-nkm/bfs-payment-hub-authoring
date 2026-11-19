import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { Markdown } from '../web/src/markdown.js';

// The assistant's replies in the panel beside every surface (I-31): a small, safe subset of markdown.
const html = (text: string) => renderToStaticMarkup(createElement(Markdown, { text }));

describe('the assistant reply renderer', () => {
  it('renders bold, inline code, a list and paragraphs', () => {
    const out = html('**Outcome:** the rule `uae.wps.rule.days-in-period.v1` passes.\n\nIt checks:\n- the **lower** bound\n- the upper bound\n\n1. Save it\n2. Ask a checker');
    expect(out).toBe(
      '<div class="md"><p><b>Outcome:</b> the rule <code>uae.wps.rule.days-in-period.v1</code> passes.</p>'
      + '<p>It checks:</p><ul><li>the <b>lower</b> bound</li><li>the upper bound</li></ul>'
      + '<ol><li>Save it</li><li>Ask a checker</li></ol></div>',
    );
  });

  it('keeps line breaks in a paragraph and shows a heading as a bold line', () => {
    expect(html('### Next steps\nOne\nTwo')).toBe('<div class="md"><p><b>Next steps</b></p><p>One<br/>Two</p></div>');
  });

  it('escapes HTML and makes no links', () => {
    const out = html('<script>alert(1)</script> and <a href="https://example.com">x</a> and [y](https://example.com) and `<b>`');
    expect(out).not.toMatch(/<script|<a /);
    expect(out).toContain('&lt;script&gt;alert(1)&lt;/script&gt;');
    expect(out).toContain('[y](https://example.com)');
    expect(out).toContain('<code>&lt;b&gt;</code>');
  });

  it('leaves code literal and unmatched marks as text', () => {
    expect(html('`**not bold**` and **open')).toBe('<div class="md"><p><code>**not bold**</code> and **open</p></div>');
  });
});
