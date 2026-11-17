# Scripts-behind

One TypeScript file per activity, named `<activity name>.ts`. A script exports
`async function run({ ctx, ports, rules, params, clock, ids, log })` and returns an outcome
name, or `{ outcome, reasonCode }`. It sees only its declared reads, may change only its
declared writes, and reaches outside only through its declared ports (Part 8, R-03-06, R-03-07).
It may not use the network, the file system, the system clock, randomness or the environment.

An activity with no script here runs the studio's reference behaviour for its type
(`src/interp/reference.ts`).
