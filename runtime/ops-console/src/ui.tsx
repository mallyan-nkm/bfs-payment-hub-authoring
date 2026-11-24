import { createContext, type ReactNode } from 'react';
import {
  Badge as FluentBadge,
  Button,
  Menu,
  MenuItemRadio,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarBody,
  Tooltip,
  type BadgeProps,
  type MessageBarIntent,
} from '@fluentui/react-components';
import { DarkThemeRegular, WeatherMoonRegular, WeatherSunnyRegular } from '@fluentui/react-icons';
import type { ThemeChoice } from './theme';

// Small Fluent UI building blocks the console's pages share (ADR 0003): status badges, messages and the theme menu.

export type Tone = 'pass' | 'fail' | 'warn' | 'info' | 'muted';
const TONES: Tone[] = ['pass', 'fail', 'warn', 'info', 'muted'];

const BADGE: Record<Tone, Pick<BadgeProps, 'color' | 'appearance'>> = {
  pass: { color: 'success', appearance: 'tint' },
  fail: { color: 'danger', appearance: 'tint' },
  warn: { color: 'warning', appearance: 'tint' },
  info: { color: 'informative', appearance: 'tint' },
  muted: { color: 'subtle', appearance: 'outline' },
};

/**
 * A status badge: Fluent's Badge, success, danger, warning or informative. It keeps the class `badge <tone>` the tests read;
 * `className` may carry the tone too, as the pages wrote it before (`badge pass`).
 */
export function Badge({ tone, className, children, ...rest }: { tone?: Tone; className?: string; children?: ReactNode; title?: string; role?: string; 'data-testid'?: string }) {
  const t = tone ?? (className?.split(/\s+/).find((c) => (TONES as string[]).includes(c)) as Tone | undefined) ?? 'info';
  const extra = (className ?? '').split(/\s+/).filter((c) => c && c !== 'badge' && c !== t).join(' ');
  return <FluentBadge {...BADGE[t]} shape="rounded" className={`badge ${t}${extra ? ` ${extra}` : ''}`} {...rest}>{children}</FluentBadge>;
}

const INTENT: Record<Tone, MessageBarIntent> = { pass: 'success', fail: 'error', warn: 'warning', info: 'info', muted: 'info' };

/** A message on the page: Fluent's MessageBar. A role of status or alert is kept, so a screen reader still hears it. */
export function Alert({ tone = 'info', className, children, ...rest }: { tone?: Tone; className?: string; children?: ReactNode; role?: string; 'data-testid'?: string }) {
  return (
    <MessageBar intent={INTENT[tone]} className={`alert ${tone}${className ? ` ${className}` : ''}`} layout="multiline" {...rest}>
      <MessageBarBody>{children}</MessageBarBody>
    </MessageBar>
  );
}

/** Two letters for a person's avatar: `Example operator` gives EO. */
export function initials(name: string): string {
  const parts = name.split(/[.\-_@\s]+/).filter(Boolean);
  return ((parts[0]?.[0] ?? '') + (parts[1]?.[0] ?? parts[0]?.[1] ?? '')).toUpperCase();
}

/** The theme the person chose, given by the root (main.tsx) to the shell's menu. */
export const ThemeChoiceContext = createContext<{ choice: ThemeChoice; setChoice: (c: ThemeChoice) => void } | null>(null);

/** Light, dark or the system's choice. */
export function ThemeMenu({ choice, onChange }: { choice: ThemeChoice; onChange: (c: ThemeChoice) => void }) {
  const icon = choice === 'dark' ? <WeatherMoonRegular /> : choice === 'light' ? <WeatherSunnyRegular /> : <DarkThemeRegular />;
  return (
    <Menu checkedValues={{ theme: [choice] }} onCheckedValueChange={(_, d) => onChange(d.checkedItems[0] as ThemeChoice)}>
      <MenuTrigger disableButtonEnhancement>
        <Tooltip content="Theme: light, dark or as the system" relationship="label">
          <Button appearance="subtle" className="topbar-button" icon={icon} data-testid="theme-menu" />
        </Tooltip>
      </MenuTrigger>
      <MenuPopover>
        <MenuList>
          <MenuItemRadio name="theme" value="system" icon={<DarkThemeRegular />}>As the system</MenuItemRadio>
          <MenuItemRadio name="theme" value="light" icon={<WeatherSunnyRegular />}>Light</MenuItemRadio>
          <MenuItemRadio name="theme" value="dark" icon={<WeatherMoonRegular />}>Dark</MenuItemRadio>
        </MenuList>
      </MenuPopover>
    </Menu>
  );
}
