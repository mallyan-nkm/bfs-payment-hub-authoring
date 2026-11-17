# Scripts-behind

One file per activity, named `<activity name>.ts` (TypeScript) or `<activity name>.py` (Python).
If both exist the TypeScript one runs.

A TypeScript script exports `async function run({ ctx, ports, rules, params, clock, ids, log })`.
A Python script defines `def run(api)` and uses `api.ctx`, `api.ports`, `api.rules`, `api.params`,
`api.clock`, `api.ids` and `api.log`. Either returns an outcome name, or an object with `outcome`
and `reasonCode`.

A script sees only its declared reads, may change only its declared writes, and reaches outside
only through its declared ports (Part 8, R-03-06, R-03-07). It may not use the network, the file
system, the system clock, randomness or the environment. TypeScript runs in a `node:vm` context;
Python runs in an isolated interpreter process with a restricted set of builtins and modules.

An activity with no script here runs the studio's reference behaviour for its type
(`src/interp/reference.ts`).
