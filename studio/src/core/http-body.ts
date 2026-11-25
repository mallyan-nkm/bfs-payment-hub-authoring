// A request's body as text. Its bytes are joined first and decoded once: a chunk of a body ends where the socket's
// read ended, which may be inside a character of two or more bytes, and a chunk decoded on its own turns that character
// into replacement characters (Issue I-408). `raw += chunk` does exactly that, so no server of the studio's reads a
// body so.

/** The body of a request (or any stream of chunks), decoded as UTF-8 once it is all read. */
export async function bodyText(req: AsyncIterable<unknown>): Promise<string> {
  const chunks: Buffer[] = [];
  for await (const c of req) chunks.push(typeof c === 'string' ? Buffer.from(c, 'utf8') : Buffer.from(c as Uint8Array));
  return Buffer.concat(chunks).toString('utf8');
}
