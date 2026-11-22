# The studio's web app

How the studio's web app (`studio/web/`) is built. It replaces the source's React UI guidelines, its React agent and skill, and the frontend's component and accessibility rules. The source's three operations apps, its shared design system and its Angular portal are not the authoring path in the north star, and their stack is not carried.

## The stack

Decided in `docs/adr/0001-studio-stack.md`:

- React 18 and Vite, TypeScript, plain CSS. No UI framework, so the look is the studio's to change.
- One single-page app over the studio's HTTP API (`studio/src/server/`).
- Colours, spacing and surfaces are CSS custom properties in `studio/web/src/styles.css`, with a dark scheme under `prefers-color-scheme`.

Do not add a component library, a CSS framework or a second icon set. A change to the stack is a new decision record, not a pull request.

## Rules

| Rule | Checked by |
| --- | --- |
| Every function of the studio works without writing code (R-10-02). | The user guide's walk-through is a browser test (`studio/test/blackbox/ui-build.test.ts`) |
| Every view fits a phone width with no horizontal scroll. | `studio/test/blackbox/ui.test.ts`, at 390 pixels wide |
| Every view loads at desktop width without errors. | `studio/test/blackbox/ui.test.ts` |
| The app never holds a secret: no token or key reaches the browser. | Review; the token is read on the server at each request |
| The acting user and tenant are shown in the header; the server enforces the role, not the app. | API tests; [access-control.md](access-control.md) |
| No colour, font or radius is written into a component. Use the custom properties. | Review |
| Text a designer reads is plain British English, without internal codes where a word will do. | Review |

## Habits from the source that still hold

- Function components, named exports, one component per concern.
- Server data is fetched through `studio/web/src/api.ts`, not from components directly.
- A `useEffect` says in a comment why it runs.
- No `any` where a type is known.

## Accessibility

The source required WCAG 2.1 AA for its operations apps. The studio's spec has no accessibility requirement. These habits cost little and are expected in a change:

- a button for an action and a link for navigation, never a clickable `div`;
- a visible focus ring on every control;
- a label for every input, and text for every icon-only button;
- colour never the only signal of a state.

Whether the studio must meet WCAG 2.1 AA, with a check, is a spec decision. Raise it as an Issue with a Recommendation before you write tests against it.
