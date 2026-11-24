# ADR 0003. The user interfaces on Fluent UI 2

Status: accepted, 24 November 2026. Supersedes the "Web app" row of ADR 0001 ("no UI framework"). Numbered 0003 because 0002 is the sessions record.

## Context

ADR 0001 kept the studio's web app free of a UI framework, "so the look is ours to change". The runtime's operations console (`runtime/ops-console`) followed it with a stylesheet of its own. Both now serve several personas (the designer, the developer, the bank's configurator and approver in the studio; the operator, supervisor, service engineer, register maker and tester on the runtime), and every control, table, tab and message was drawn by hand. The owner compared three mock-ups and chose "A · Fluent UI 2 style": a deep navy top bar with the product and console names, a global search, status badges, notifications and the user's initials; a left navigation with icons; page headers with a context line and primary and secondary buttons; summary cards; a data table with status badges and filters; a detail panel with a vertical timeline.

## Decision

Both apps use Microsoft Fluent UI 2 for React, v9:

| Package | Version | Published |
| --- | --- | --- |
| `@fluentui/react-components` | 9.75.1 | 2 November 2026 |
| `@fluentui/react-icons` | 2.0.345 | 30 October 2026 |

The versions are pinned exactly, in `devDependencies` (the web app is bundled; the server does not load it), and the lock files were resolved with `npm install --before=2026-11-09`, so every package Fluent brings is at least two weeks old.

- **One brand theme.** `theme.ts` builds a `BrandVariants` ramp around navy `#0f4c81` (shade 80) and makes the light and dark themes with `createLightTheme` and `createDarkTheme`; in dark, brand text and fills move up the ramp to keep their contrast. The top bar is `#0f2d4f` in both themes and the teal `#5fd3c5` marks the product. The font is `'Segoe UI', 'Noto Sans', system-ui`. The file is copied, byte for byte, into `studio/web/src/theme.ts` and `runtime/ops-console/src/theme.ts`: the two apps have separate packages and lock files, and a shared module would have needed a third package or path aliases in both builds. `studio/test/ui-theme.test.ts` and `runtime/ops-console/test/unit/theme.test.ts` fail when the copies differ.
- **Light and dark** follow the operating system; a menu in the top bar (`data-testid="theme-menu"`) sets light, dark or "as the system", kept in the browser's local storage only.
- **Fluent components replace hand-made controls**: Button, Link, Input, SearchBox, Select (the native select, so keyboard, forms and `selectOption` work as before), Textarea, Checkbox, Radio and RadioGroup, ToggleButton, Tab and TabList, Table, Badge and CounterBadge, Card, MessageBar, Dialog, Menu, Tooltip, Spinner, Avatar, Breadcrumb, Nav and NavItem, Toolbar.
- **Stylesheets keep only the layout Fluent lacks**: the shell grid, the workflow canvas and outline, the debugger, the assistant dock, the timeline. Their colours are Fluent's tokens (`var(--colorNeutralBackground1)` and so on), aliased to the names the stylesheets already used, so they follow the theme.
- **Content security policy.** Fluent (through Griffel) writes its styles into `<style>` elements at run time. The pages' policies kept `style-src 'self'`, which blocks them. Each answer with a page now carries a fresh nonce: the studio's server and the runtime hosts put it in the page's `<meta name="th-style-nonce">` and in `style-src 'self' 'nonce-…'`, and the page gives it to Fluent's renderer (`createDOMRenderer(document, { styleElementAttributes: { nonce } })`). `unsafe-inline` stays out and scripts are still the page's own files only. Issue I-290 asks for the requirements' words to say so.
- **Selectors for tests and the demo recorder** stay stable: the native elements Fluent renders keep their `name`, `aria-label` and label associations; classes the scripts read (`.alert`, `.badge`, `.panel`, `.cv-*`, `.dbg-*`, `nav.sidebar`, `nav.side`) stay on the Fluent components; and new `data-testid`s name what had none (`nav-label`, `theme-menu`).

## Consequences

- The look follows Fluent's design language and its accessibility work (focus, keyboard, contrast, high contrast), and both apps look alike.
- The web bundles are larger (about 1 MB minified for the studio before compression); the apps are internal tools on a local network.
- A page served by anything other than the studio's server or `SecurityHeaders.WriteConsolePageAsync` must add the nonce itself, or the page draws unstyled.
- A Fluent upgrade is a deliberate change: pick versions at least two weeks old, re-run both apps' builds, unit tests and browser tests.
