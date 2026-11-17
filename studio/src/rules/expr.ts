// The rule expression language (Part 4, 4.3; C06-S1).
// Literals, paths on bound variables, comparison, and/or/not, arithmetic, `in`, lambdas for
// aggregate functions, and a fixed library of pure functions. Side-effect free and deterministic.

export type Node =
  | { t: 'num'; v: number }
  | { t: 'str'; v: string }
  | { t: 'bool'; v: boolean }
  | { t: 'null' }
  | { t: 'list'; items: Node[] }
  | { t: 'path'; parts: string[] }
  | { t: 'call'; fn: string; args: Node[] }
  | { t: 'un'; op: 'not' | '-'; e: Node }
  | { t: 'bin'; op: string; l: Node; r: Node }
  | { t: 'lambda'; param: string; body: Node };

export class ExprError extends Error {}

interface Tok { k: 'num' | 'str' | 'id' | 'op' | 'eof'; v: string; pos: number }

function lex(src: string): Tok[] {
  const out: Tok[] = [];
  let i = 0;
  while (i < src.length) {
    const c = src[i];
    if (/\s/.test(c)) { i++; continue; }
    if (/[0-9]/.test(c)) {
      let j = i;
      while (j < src.length && /[0-9.]/.test(src[j])) j++;
      out.push({ k: 'num', v: src.slice(i, j), pos: i });
      i = j;
      continue;
    }
    if (c === "'" || c === '"') {
      let j = i + 1;
      let s = '';
      while (j < src.length && src[j] !== c) {
        if (src[j] === '\\' && j + 1 < src.length) { s += src[j + 1]; j += 2; continue; }
        s += src[j++];
      }
      if (j >= src.length) throw new ExprError(`Unterminated string at ${i}`);
      out.push({ k: 'str', v: s, pos: i });
      i = j + 1;
      continue;
    }
    if (/[A-Za-z_]/.test(c)) {
      let j = i;
      while (j < src.length && /[A-Za-z0-9_]/.test(src[j])) j++;
      out.push({ k: 'id', v: src.slice(i, j), pos: i });
      i = j;
      continue;
    }
    const two = src.slice(i, i + 2);
    if (['==', '!=', '<=', '>=', '=>', '&&', '||'].includes(two)) { out.push({ k: 'op', v: two, pos: i }); i += 2; continue; }
    if ('<>+-*/%(),.[]!'.includes(c)) { out.push({ k: 'op', v: c, pos: i }); i++; continue; }
    throw new ExprError(`Unexpected character ${c} at ${i}`);
  }
  out.push({ k: 'eof', v: '', pos: src.length });
  return out;
}

const BIN_PREC: Record<string, number> = {
  or: 1, '||': 1, and: 2, '&&': 2,
  '==': 4, '!=': 4, '<': 5, '<=': 5, '>': 5, '>=': 5, in: 5,
  '+': 6, '-': 6, '*': 7, '/': 7, '%': 7,
};

export function parse(src: string): Node {
  const toks = lex(src);
  let p = 0;
  const peek = () => toks[p];
  const next = () => toks[p++];
  const expect = (v: string) => {
    const t = next();
    if (t.v !== v) throw new ExprError(`Expected ${v} at ${t.pos} in: ${src}`);
  };

  function binOp(): string | null {
    const t = peek();
    if (t.k === 'op' && t.v in BIN_PREC) return t.v;
    if (t.k === 'id' && (t.v === 'and' || t.v === 'or' || t.v === 'in')) return t.v;
    return null;
  }

  function expr(minPrec = 0): Node {
    let left = unary();
    for (;;) {
      const op = binOp();
      if (!op) break;
      const prec = BIN_PREC[op];
      if (prec < minPrec) break;
      next();
      const right = expr(prec + 1);
      left = { t: 'bin', op: op === '&&' ? 'and' : op === '||' ? 'or' : op, l: left, r: right };
    }
    return left;
  }

  function unary(): Node {
    const t = peek();
    if ((t.k === 'id' && t.v === 'not') || (t.k === 'op' && t.v === '!')) {
      next();
      // `not` binds looser than comparison: `not a == b` is `not (a == b)`.
      return { t: 'un', op: 'not', e: expr(4) };
    }
    if (t.k === 'op' && t.v === '-') { next(); return { t: 'un', op: '-', e: unary() }; }
    return postfix(primary());
  }

  function postfix(n: Node): Node {
    for (;;) {
      const t = peek();
      if (t.k === 'op' && t.v === '.' && n.t === 'path') {
        next();
        const id = next();
        if (id.k !== 'id') throw new ExprError(`Expected a name after . at ${id.pos}`);
        n = { t: 'path', parts: [...n.parts, id.v] };
        continue;
      }
      return n;
    }
  }

  function primary(): Node {
    const t = next();
    if (t.k === 'num') return { t: 'num', v: Number(t.v) };
    if (t.k === 'str') return { t: 'str', v: t.v };
    if (t.k === 'op' && t.v === '(') {
      const e = expr();
      expect(')');
      return e;
    }
    if (t.k === 'op' && t.v === '[') {
      const items: Node[] = [];
      if (peek().v !== ']') {
        items.push(expr());
        while (peek().v === ',') { next(); items.push(expr()); }
      }
      expect(']');
      return { t: 'list', items };
    }
    if (t.k === 'id') {
      if (t.v === 'true' || t.v === 'false') return { t: 'bool', v: t.v === 'true' };
      if (t.v === 'null') return { t: 'null' };
      // Lambda: x => body
      if (peek().v === '=>') {
        next();
        return { t: 'lambda', param: t.v, body: expr() };
      }
      if (peek().v === '(') {
        next();
        const args: Node[] = [];
        if (peek().v !== ')') {
          args.push(expr());
          while (peek().v === ',') { next(); args.push(expr()); }
        }
        expect(')');
        return { t: 'call', fn: t.v, args };
      }
      return { t: 'path', parts: [t.v] };
    }
    throw new ExprError(`Unexpected ${t.v || 'end'} at ${t.pos} in: ${src}`);
  }

  const n = expr();
  if (peek().k !== 'eof') throw new ExprError(`Unexpected ${peek().v} at ${peek().pos} in: ${src}`);
  return n;
}

// ------------------------------------------------------------------ evaluation

export type Env = Record<string, any>;

function isBlank(v: any) {
  return v === undefined || v === null || (typeof v === 'string' && v.trim() === '') || (Array.isArray(v) && v.length === 0);
}

/** ISO 13616 IBAN check digits (mod 97). */
export function ibanValid(iban: any): boolean {
  if (typeof iban !== 'string') return false;
  const s = iban.replace(/\s+/g, '').toUpperCase();
  if (!/^[A-Z]{2}[0-9]{2}[A-Z0-9]{10,30}$/.test(s)) return false;
  const r = s.slice(4) + s.slice(0, 4);
  let rem = 0;
  for (const ch of r) {
    const d = /[A-Z]/.test(ch) ? String(ch.charCodeAt(0) - 55) : ch;
    for (const x of d) rem = (rem * 10 + Number(x)) % 97;
  }
  return rem === 1;
}

const FUNCS: Record<string, (args: any[], lambdas: ((x: any) => any)[]) => any> = {
  isBlank: ([v]) => isBlank(v),
  len: ([v]) => (v === undefined || v === null ? 0 : typeof v === 'string' || Array.isArray(v) ? v.length : String(v).length),
  startsWith: ([s, p]) => typeof s === 'string' && typeof p === 'string' && s.startsWith(p),
  matches: ([s, re]) => s !== undefined && s !== null && new RegExp(re).test(String(s)),
  count: ([xs]) => (Array.isArray(xs) ? xs.length : 0),
  sum: ([xs], [f]) => (Array.isArray(xs) ? xs.reduce((a, x) => a + num(f ? f(x) : x), 0) : 0),
  min: ([xs], [f]) => (Array.isArray(xs) && xs.length ? Math.min(...xs.map((x) => num(f ? f(x) : x))) : null),
  max: ([xs], [f]) => (Array.isArray(xs) && xs.length ? Math.max(...xs.map((x) => num(f ? f(x) : x))) : null),
  concat: (args) => args.map((a) => (a === undefined || a === null ? '' : String(a))).join(''),
  ibanValid: ([v]) => ibanValid(v),
  in: ([x, xs]) => Array.isArray(xs) && xs.some((y) => eq(x, y)),
};

export const FUNCTION_NAMES = Object.keys(FUNCS);

function num(v: any): number {
  if (v === undefined || v === null || v === '') return 0;
  const n = Number(v);
  return Number.isNaN(n) ? 0 : n;
}

function eq(a: any, b: any): boolean {
  if (a === undefined) a = null;
  if (b === undefined) b = null;
  if (typeof a === 'number' || typeof b === 'number') {
    if (a === null || b === null) return a === b;
    return Number(a) === Number(b);
  }
  return JSON.stringify(a) === JSON.stringify(b);
}

function cmp(a: any, b: any): number {
  if (typeof a === 'number' || typeof b === 'number') return num(a) - num(b);
  return String(a ?? '').localeCompare(String(b ?? ''));
}

export function evaluate(n: Node, env: Env): any {
  switch (n.t) {
    case 'num': case 'str': case 'bool': return n.v;
    case 'null': return null;
    case 'list': return n.items.map((x) => evaluate(x, env));
    case 'path': {
      let cur: any = env[n.parts[0]];
      for (const p of n.parts.slice(1)) {
        if (cur === undefined || cur === null) return undefined;
        cur = cur[p];
      }
      return cur;
    }
    case 'lambda': return (x: any) => evaluate(n.body, { ...env, [n.param]: x });
    case 'call': {
      const f = FUNCS[n.fn];
      if (!f) throw new ExprError(`Unknown function ${n.fn}`);
      const vals: any[] = [];
      const lambdas: ((x: any) => any)[] = [];
      for (const a of n.args) {
        if (a.t === 'lambda') lambdas.push(evaluate(a, env));
        else vals.push(evaluate(a, env));
      }
      return f(vals, lambdas);
    }
    case 'un': {
      const v = evaluate(n.e, env);
      return n.op === 'not' ? !truthy(v) : -num(v);
    }
    case 'bin': {
      if (n.op === 'and') return truthy(evaluate(n.l, env)) && truthy(evaluate(n.r, env));
      if (n.op === 'or') return truthy(evaluate(n.l, env)) || truthy(evaluate(n.r, env));
      const l = evaluate(n.l, env);
      const r = evaluate(n.r, env);
      switch (n.op) {
        case '==': return eq(l, r);
        case '!=': return !eq(l, r);
        case '<': return l !== undefined && l !== null && cmp(l, r) < 0;
        case '<=': return l !== undefined && l !== null && cmp(l, r) <= 0;
        case '>': return l !== undefined && l !== null && cmp(l, r) > 0;
        case '>=': return l !== undefined && l !== null && cmp(l, r) >= 0;
        case 'in': return Array.isArray(r) && r.some((y) => eq(l, y));
        case '+': return typeof l === 'string' && typeof r === 'string' ? l + r : round(num(l) + num(r));
        case '-': return round(num(l) - num(r));
        case '*': return round(num(l) * num(r));
        case '/': return num(r) === 0 ? null : num(l) / num(r);
        case '%': return num(l) % num(r);
      }
    }
  }
  throw new ExprError(`Cannot evaluate ${(n as any).t}`);
}

/** Money arithmetic: round to 1e-9 to avoid binary noise such as 0.1 + 0.2. */
function round(x: number): number {
  return Math.round(x * 1e9) / 1e9;
}

export function truthy(v: any): boolean {
  return !(v === false || v === null || v === undefined || v === 0 || v === '');
}

const cache = new Map<string, Node>();
export function compile(src: string): Node {
  let n = cache.get(src);
  if (!n) {
    n = parse(src);
    cache.set(src, n);
  }
  return n;
}

export function evalExpr(src: string, env: Env): any {
  return evaluate(compile(src), env);
}

/** The free variables of an expression: names it reads that are not bound by a lambda. */
export function freeNames(n: Node, bound: Set<string> = new Set()): Set<string> {
  const out = new Set<string>();
  const go = (x: Node, b: Set<string>) => {
    switch (x.t) {
      case 'path': if (!b.has(x.parts[0])) out.add(x.parts.join('.')); break;
      case 'list': x.items.forEach((i) => go(i, b)); break;
      case 'call': x.args.forEach((a) => go(a, b)); break;
      case 'un': go(x.e, b); break;
      case 'bin': go(x.l, b); go(x.r, b); break;
      case 'lambda': go(x.body, new Set([...b, x.param])); break;
    }
  };
  go(n, bound);
  return out;
}

/** Interpolate {expr} in a message template. */
export function interpolate(template: string, env: Env): string {
  return template.replace(/\{([^}]+)\}/g, (_, e) => {
    try {
      const v = evalExpr(e, env);
      return v === undefined || v === null ? '' : String(v);
    } catch {
      return `{${e}}`;
    }
  });
}
