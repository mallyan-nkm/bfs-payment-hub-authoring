# The user interfaces, before and after Fluent UI 2

The studio (`studio/web`) and the runtime's web app (`runtime/ops-console`: the operations console, registers, service
console and test bench) moved to Microsoft Fluent UI 2 on 24 November 2026, as `docs/adr/0003-ui-fluent.md` records. The
screenshots below are of the main screens, at 1440 × 900 with a device scale factor of 2, quantised to 256 colours.

The studio's screens were taken on the studio's own server against the fake GitHub seeded from `content-configurations/`
(the black-box harness, `studio/test/blackbox/server.ts`), signed in through the development provider. The operations
console's were taken on its built page with synthetic answers played to it in place of the console's API (made-up ids,
file names and amounts; no runtime). All data is synthetic.

## The studio

| Screen | Who sees it | Before | After |
| --- | --- | --- | --- |
| Catalogue: every artefact, searched by name, title or path, with status badges | every role | [before](screenshots/studio-before-catalogue.png) | [after](screenshots/studio-after-catalogue.png) |
| Workflow designer: the YAML beside the canvas, the static checks, the explain panel | designer | [before](screenshots/studio-before-designer.png) | [after](screenshots/studio-after-designer.png), [dark](screenshots/studio-after-designer-dark.png) |
| Artefact editors: a rule in a form, its gates, save and release | developer | [before](screenshots/studio-before-editors.png) | [after](screenshots/studio-after-editors.png) |
| Test bench: the tests, run and debugged | designer, developer | [before](screenshots/studio-before-testbench.png) | [after](screenshots/studio-after-testbench.png) |
| Bank workspace: hooks, rules, dials, simulators, bindings, the bench by workflow | bank configurator | [before](screenshots/studio-before-bank.png) | [after](screenshots/studio-after-bank.png) |

## The operations console

| Screen | Who sees it | Before | After |
| --- | --- | --- | --- |
| Payments: filters, saved filters, summary cards, quick status filters, the list with status badges | operator, supervisor | [before](screenshots/ops-before-payments.png) | [after](screenshots/ops-after-payments.png) |
| A payment: breadcrumb, badges, actions, declared fields in cards, tabs, the timeline | operator, supervisor | [before](screenshots/ops-before-detail.png) | [after](screenshots/ops-after-detail.png), [dark](screenshots/ops-after-detail-dark.png) |
| Work queue: the queue's tabs, bulk take, escalate and release | operator, supervisor | [before](screenshots/ops-before-work.png) | [after](screenshots/ops-after-work.png) |
| Reports: totals as cards, by scheme and status, the fixed reports | supervisor | [before](screenshots/ops-before-reports.png) | [after](screenshots/ops-after-reports.png) |

## What changed on every screen

- A navy top bar (`#0f2d4f`) with the product and the console's name, a search (the studio's catalogue; a payment by id,
  reference or file in the operations console), status badges (shared content or the bank tenant and "design time" in the
  studio; the read model's lag and "Rates stale" in the operations console), a link to the work queue, the theme menu
  (light, dark or as the system), the user's initials and Sign out.
- A Fluent navigation with an icon per surface or section and a selected indicator; on a phone it opens from the menu
  button.
- Fluent buttons, inputs, search boxes, selects, checkboxes, radios, switches as toggle buttons, tabs, tables, badges,
  cards, message bars, dialogs, menus, tooltips and spinners, in one brand theme (`theme.ts`, navy `#0f4c81`, teal
  `#5fd3c5`, Segoe UI).
- The workflow canvas, outline, debugger and assistant dock keep their own layout, drawn in Fluent's tokens.
