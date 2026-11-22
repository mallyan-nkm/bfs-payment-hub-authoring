# Scripts-behind

The factory's script-behinds, authoring source. The studio does not read this folder: the seed
(`npm run studio -- seed-content`) publishes each file to the content branch's `scripts/`, where the
studio reads it (R-11-25). After a change, run `npm run studio -- seed-content --write-local`; a
connected branch takes a changed factory script only with a new release.

One file per activity, named `<activity name>.ts` (TypeScript) or `<activity name>.py` (Python).
If both exist the TypeScript one runs.

A TypeScript script exports `async function run({ ctx, ports, rules, params, clock, ids, log })`.
A Python script defines `def run(api)` and uses `api.ctx`, `api.ports`, `api.rules`, `api.params`,
`api.clock`, `api.ids` and `api.log`. Either returns an outcome name, or an object with `outcome`
and `reasonCode`.

Money is exact (R-02-17, R-04-23): do sums and comparisons of amounts with `rules.money`
(`api.rules.money` in Python), whose `add`, `sub`, `mul`, `div`, `sum`, `round2`, `cmp`, `eq` and
`str` compute on exact decimals as the rule language does, never with `+` on JavaScript numbers or
Python floats.

A script sees only its declared reads, may change only its declared writes, and reaches outside
only through its declared ports (Part 8, R-03-06, R-03-07). It may not use the network, the file
system, the system clock, randomness or the environment. TypeScript runs in a `node:vm` context;
Python runs in an isolated interpreter process with a restricted set of builtins and modules.

An activity with no script here runs the studio's reference behaviour for its type
(`src/interp/reference.ts`).
