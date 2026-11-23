// A synthetic .NET assembly for the studio's tests, which run without the .NET SDK: a PE file with the metadata tables the
// studio reads (Module, Assembly and AssemblyRef, ECMA-335 partition II), naming the assembly and the assemblies it references.
// No runtime loads it: it has no code. `overlay` is appended after the image, so two builds of one module differ in their bytes.

export function syntheticAssembly(name: string, refs: { name: string; version?: string }[], overlay = ''): Buffer {
  // #Strings: the empty string, then each name.
  const names = [name, ...refs.map((r) => r.name)];
  const strParts: Buffer[] = [Buffer.from([0])];
  const offsets: number[] = [];
  let at = 1;
  for (const n of names) { offsets.push(at); const b = Buffer.from(`${n}\0`, 'utf8'); strParts.push(b); at += b.length; }
  const pad4 = (b: Buffer) => Buffer.concat([b, Buffer.alloc((4 - (b.length % 4)) % 4)]);
  const strings = pad4(Buffer.concat(strParts));
  const guid = Buffer.alloc(16, 0x11);
  const blob = Buffer.alloc(4);
  // #~: Module (0x00), Assembly (0x20) and AssemblyRef (0x23), every index two bytes.
  const u2 = (v: number) => { const b = Buffer.alloc(2); b.writeUInt16LE(v); return b; };
  const u4 = (v: number) => { const b = Buffer.alloc(4); b.writeUInt32LE(v); return b; };
  const head = Buffer.alloc(24);
  head.writeUInt8(2, 4);
  head.writeUInt8(1, 7);
  head.writeBigUInt64LE((1n << 0n) | (1n << 32n) | (1n << 35n), 8);
  const rows = Buffer.concat([u4(1), u4(1), u4(refs.length)]);
  const module = Buffer.concat([u2(0), u2(offsets[0]), u2(1), u2(0), u2(0)]);
  const assembly = Buffer.concat([u4(0x8004), u2(1), u2(0), u2(0), u2(0), u4(0), u2(0), u2(offsets[0]), u2(0)]);
  const assemblyRefs = refs.map((r, i) => {
    const [a, b, c] = (r.version ?? '1.0.0').split('.').map(Number);
    return Buffer.concat([u2(a), u2(b), u2(c), u2(0), u4(0), u2(0), u2(offsets[i + 1]), u2(0), u2(0)]);
  });
  const tables = pad4(Buffer.concat([head, rows, module, assembly, ...assemblyRefs]));
  // The metadata root and its stream headers.
  const version = pad4(Buffer.from('v4.0.30319\0', 'ascii'));
  const streams: [string, Buffer][] = [['#~', tables], ['#Strings', strings], ['#GUID', guid], ['#Blob', blob]];
  const headerOf = (n: string) => pad4(Buffer.from(`${n}\0`, 'ascii'));
  const rootFixed = 16 + version.length + 4;
  let offset = rootFixed + streams.reduce((n, [s]) => n + 8 + headerOf(s).length, 0);
  const headers: Buffer[] = [];
  for (const [s, b] of streams) { headers.push(Buffer.concat([u4(offset), u4(b.length), headerOf(s)])); offset += b.length; }
  const root = Buffer.concat([u4(0x424a5342), u2(1), u2(1), u4(0), u4(version.length), version, u2(0), u2(streams.length), ...headers, ...streams.map(([, b]) => b)]);
  // The PE image: one section at RVA 0x2000, file offset 0x200, holding the CLI header and the metadata.
  const cli = Buffer.alloc(72);
  cli.writeUInt32LE(72, 0);
  cli.writeUInt16LE(2, 4);
  cli.writeUInt16LE(5, 6);
  cli.writeUInt32LE(0x2000 + 72, 8);
  cli.writeUInt32LE(root.length, 12);
  const text = pad4(Buffer.concat([cli, root]));
  const file = Buffer.alloc(0x200);
  file.write('MZ', 0, 'ascii');
  file.writeUInt32LE(0x80, 0x3c);
  file.write('PE\0\0', 0x80, 'ascii');
  file.writeUInt16LE(0x14c, 0x84);
  file.writeUInt16LE(1, 0x86);
  file.writeUInt16LE(224, 0x94);
  const opt = 0x98;
  file.writeUInt16LE(0x10b, opt);
  file.writeUInt32LE(16, opt + 92);
  file.writeUInt32LE(0x2000, opt + 96 + 14 * 8);
  file.writeUInt32LE(72, opt + 96 + 14 * 8 + 4);
  const sec = opt + 224;
  file.write('.text', sec, 'ascii');
  file.writeUInt32LE(text.length, sec + 8);
  file.writeUInt32LE(0x2000, sec + 12);
  file.writeUInt32LE(text.length, sec + 16);
  file.writeUInt32LE(0x200, sec + 20);
  return Buffer.concat([file, text, Buffer.from(overlay, 'utf8')]);
}
