import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { openEnv, type StudioEnv } from '../src/env.js';

/** A studio environment over the spec, with an empty, private workspace. */
export function freshEnv(): StudioEnv {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'th-studio-'));
  return openEnv({ dataRoot: dir });
}

export function clone<T>(x: T): T {
  return JSON.parse(JSON.stringify(x));
}
