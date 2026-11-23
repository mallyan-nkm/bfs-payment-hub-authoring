// The assemblies a .NET assembly references, read from its metadata (ECMA-335 partition II, sections 24 and 22) without loading
// it: the studio's half of R-09-125, that a native module references the native contract and no other assembly of TransactHub. The
// runtime reads the same table with System.Reflection.Metadata (TransactHub.Store.NativeAssemblies.References).

export interface AssemblyReference { name: string; version: string }

type Col = 'u2' | 'u4' | 'str' | 'guid' | 'blob' | number | string;

// Coded indexes: the tag bits and the tables each may point at (-1 for an unused tag).
const CODED: Record<string, [number, number[]]> = {
  TypeDefOrRef: [2, [0x02, 0x01, 0x1b]],
  HasConstant: [2, [0x04, 0x08, 0x17]],
  HasCustomAttribute: [5, [0x06, 0x04, 0x01, 0x02, 0x08, 0x09, 0x0a, 0x00, 0x0e, 0x17, 0x14, 0x11, 0x1a, 0x1b, 0x20, 0x23, 0x26, 0x27, 0x28, 0x2a, 0x2c, 0x2b]],
  HasFieldMarshal: [1, [0x04, 0x08]],
  HasDeclSecurity: [2, [0x02, 0x06, 0x20]],
  MemberRefParent: [3, [0x02, 0x01, 0x1a, 0x06, 0x1b]],
  HasSemantics: [1, [0x14, 0x17]],
  MethodDefOrRef: [1, [0x06, 0x0a]],
  MemberForwarded: [1, [0x04, 0x06]],
  Implementation: [2, [0x26, 0x23, 0x27]],
  CustomAttributeType: [3, [-1, -1, 0x06, 0x0a, -1]],
  ResolutionScope: [2, [0x00, 0x1a, 0x23, 0x01]],
  TypeOrMethodDef: [1, [0x02, 0x06]],
};

// The columns of every table before AssemblyRef (0x23), and AssemblyRef's own: a number is an index into that table.
const TABLES: Col[][] = [
  ['u2', 'str', 'guid', 'guid', 'guid'], // 0x00 Module
  ['ResolutionScope', 'str', 'str'], // 0x01 TypeRef
  ['u4', 'str', 'str', 'TypeDefOrRef', 0x04, 0x06], // 0x02 TypeDef
  [0x04], // 0x03 FieldPtr
  ['u2', 'str', 'blob'], // 0x04 Field
  [0x06], // 0x05 MethodPtr
  ['u4', 'u2', 'u2', 'str', 'blob', 0x08], // 0x06 MethodDef
  [0x08], // 0x07 ParamPtr
  ['u2', 'u2', 'str'], // 0x08 Param
  [0x02, 'TypeDefOrRef'], // 0x09 InterfaceImpl
  ['MemberRefParent', 'str', 'blob'], // 0x0A MemberRef
  ['u2', 'HasConstant', 'blob'], // 0x0B Constant
  ['HasCustomAttribute', 'CustomAttributeType', 'blob'], // 0x0C CustomAttribute
  ['HasFieldMarshal', 'blob'], // 0x0D FieldMarshal
  ['u2', 'HasDeclSecurity', 'blob'], // 0x0E DeclSecurity
  ['u2', 'u4', 0x02], // 0x0F ClassLayout
  ['u4', 0x04], // 0x10 FieldLayout
  ['blob'], // 0x11 StandAloneSig
  [0x02, 0x14], // 0x12 EventMap
  [0x14], // 0x13 EventPtr
  ['u2', 'str', 'TypeDefOrRef'], // 0x14 Event
  [0x02, 0x17], // 0x15 PropertyMap
  [0x17], // 0x16 PropertyPtr
  ['u2', 'str', 'blob'], // 0x17 Property
  ['u2', 0x06, 'HasSemantics'], // 0x18 MethodSemantics
  [0x02, 'MethodDefOrRef', 'MethodDefOrRef'], // 0x19 MethodImpl
  ['str'], // 0x1A ModuleRef
  ['blob'], // 0x1B TypeSpec
  ['u2', 'MemberForwarded', 'str', 0x1a], // 0x1C ImplMap
  ['u4', 0x04], // 0x1D FieldRVA
  ['u4', 'u4'], // 0x1E EncLog
  ['u4'], // 0x1F EncMap
  ['u4', 'u2', 'u2', 'u2', 'u2', 'u4', 'blob', 'str', 'str'], // 0x20 Assembly
  ['u4'], // 0x21 AssemblyProcessor
  ['u4', 'u4', 'u4'], // 0x22 AssemblyOS
  ['u2', 'u2', 'u2', 'u2', 'u4', 'blob', 'str', 'str', 'blob'], // 0x23 AssemblyRef
];
const ASSEMBLY_REF = 0x23;

/** The assemblies a .NET assembly references, with their versions; undefined when the bytes are not a .NET assembly. */
export function assemblyReferences(b: Buffer): AssemblyReference[] | undefined {
  try {
    return read(b);
  } catch {
    return undefined;
  }
}

function read(b: Buffer): AssemblyReference[] | undefined {
  if (b.length < 0x40 || b.readUInt16LE(0) !== 0x5a4d) return undefined; // MZ
  const pe = b.readUInt32LE(0x3c);
  if (b.readUInt32LE(pe) !== 0x00004550) return undefined; // PE\0\0
  const sections = b.readUInt16LE(pe + 6);
  const optSize = b.readUInt16LE(pe + 20);
  const opt = pe + 24;
  const magic = b.readUInt16LE(opt);
  const dirs = opt + (magic === 0x20b ? 112 : magic === 0x10b ? 96 : NaN);
  if (Number.isNaN(dirs)) return undefined;
  const cliRva = b.readUInt32LE(dirs + 14 * 8);
  if (!cliRva) return undefined;
  const table = opt + optSize;
  const at = (rva: number) => {
    for (let i = 0; i < sections; i++) {
      const s = table + i * 40;
      const va = b.readUInt32LE(s + 12), size = Math.max(b.readUInt32LE(s + 8), b.readUInt32LE(s + 16)), raw = b.readUInt32LE(s + 20);
      if (rva >= va && rva < va + size) return rva - va + raw;
    }
    throw new Error(`RVA ${rva} is in no section`);
  };
  const cli = at(cliRva);
  const md = at(b.readUInt32LE(cli + 8));
  if (b.readUInt32LE(md) !== 0x424a5342) return undefined; // BSJB
  const verLen = b.readUInt32LE(md + 12);
  let p = md + 16 + verLen + 2;
  const streamCount = b.readUInt16LE(p);
  p += 2;
  const streams = new Map<string, { offset: number; size: number }>();
  for (let i = 0; i < streamCount; i++) {
    const offset = b.readUInt32LE(p), size = b.readUInt32LE(p + 4);
    let e = p + 8;
    while (b[e] !== 0) e++;
    streams.set(b.toString('ascii', p + 8, e), { offset: md + offset, size });
    p = p + 8 + (((e - (p + 8)) + 4) & ~3);
  }
  const tilde = streams.get('#~') ?? streams.get('#-');
  const strings = streams.get('#Strings');
  if (!tilde || !strings) return undefined;
  const t = tilde.offset;
  const heap = b[t + 6];
  const valid = b.readBigUInt64LE(t + 8);
  const rows: number[] = new Array(64).fill(0);
  let q = t + 24;
  for (let i = 0; i < 64; i++) if ((valid >> BigInt(i)) & 1n) { rows[i] = b.readUInt32LE(q); q += 4; }
  if (!rows[ASSEMBLY_REF]) return [];
  const strIdx = heap & 1 ? 4 : 2, guidIdx = heap & 2 ? 4 : 2, blobIdx = heap & 4 ? 4 : 2;
  const size = (c: Col): number => {
    if (c === 'u2') return 2;
    if (c === 'u4') return 4;
    if (c === 'str') return strIdx;
    if (c === 'guid') return guidIdx;
    if (c === 'blob') return blobIdx;
    if (typeof c === 'number') return rows[c] < 0x10000 ? 2 : 4;
    const [bits, targets] = CODED[c];
    return Math.max(...targets.map((x) => (x < 0 ? 0 : rows[x]))) < 1 << (16 - bits) ? 2 : 4;
  };
  for (let i = 0; i < ASSEMBLY_REF; i++) q += rows[i] * TABLES[i].reduce((n: number, c) => n + size(c), 0);
  const str = (i: number) => {
    let e = strings.offset + i;
    while (b[e] !== 0) e++;
    return b.toString('utf8', strings.offset + i, e);
  };
  const idx = (o: number, n: number) => (n === 2 ? b.readUInt16LE(o) : b.readUInt32LE(o));
  const out: AssemblyReference[] = [];
  const rowSize = TABLES[ASSEMBLY_REF].reduce((n: number, c) => n + size(c), 0);
  for (let r = 0; r < rows[ASSEMBLY_REF]; r++) {
    const o = q + r * rowSize;
    const version = [0, 2, 4].map((k) => b.readUInt16LE(o + k)).join('.');
    const name = str(idx(o + 12 + blobIdx, strIdx));
    out.push({ name, version });
  }
  return out;
}
