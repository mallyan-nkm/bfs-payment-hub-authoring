// The TransactHub brand theme for Fluent UI 2 (ADR 0003). One file, kept byte for byte the same in the studio
// (studio/web/src/theme.ts) and the operations console (runtime/ops-console/src/theme.ts); a test checks the two copies.
import { useEffect, useState } from 'react';
import { createDarkTheme, createLightTheme, type BrandVariants, type Theme } from '@fluentui/react-components';

/** The navy brand ramp, built around #0f4c81 (shade 80). */
export const brand: BrandVariants = {
  10: '#020a13',
  20: '#04172b',
  30: '#06223f',
  40: '#082c52',
  50: '#0a3561',
  60: '#0c3e6f',
  70: '#0d4578',
  80: '#0f4c81',
  90: '#2a5f92',
  100: '#4472a2',
  110: '#5e86b2',
  120: '#789ac2',
  130: '#93afd1',
  140: '#aec4df',
  150: '#c9d9ec',
  160: '#e4edf7',
};

/** The deep navy of the top bar, the same in light and dark. */
export const topBar = '#0f2d4f';
/** The teal accent: the top bar's mark and the selected indicator on navy. */
export const accent = '#5fd3c5';

const fontFamilyBase = "'Segoe UI', 'Noto Sans', system-ui, sans-serif";
const fontFamilyMonospace = "Consolas, 'Cascadia Mono', 'Noto Sans Mono', ui-monospace, monospace";

export const lightTheme: Theme = { ...createLightTheme(brand), fontFamilyBase, fontFamilyMonospace };

export const darkTheme: Theme = {
  ...createDarkTheme(brand),
  fontFamilyBase,
  fontFamilyMonospace,
  // Brand text and fills lighter on dark, so they keep their contrast.
  colorBrandForeground1: brand[120],
  colorBrandForeground2: brand[130],
  colorBrandForegroundLink: brand[130],
  colorBrandForegroundLinkHover: brand[140],
  colorBrandBackground: brand[100],
  colorBrandBackgroundHover: brand[110],
  colorBrandBackgroundPressed: brand[90],
  colorBrandBackgroundSelected: brand[100],
  colorCompoundBrandStroke: brand[120],
  colorCompoundBrandBackground: brand[120],
  colorNeutralForeground2BrandSelected: brand[130],
  colorNeutralForeground2BrandHover: brand[130],
};

export type ThemeChoice = 'system' | 'light' | 'dark';
const KEY = 'th-theme';

function stored(): ThemeChoice {
  try {
    const v = window.localStorage.getItem(KEY);
    return v === 'light' || v === 'dark' ? v : 'system';
  } catch {
    return 'system';
  }
}

function osDark(): boolean {
  try {
    return window.matchMedia('(prefers-color-scheme: dark)').matches;
  } catch {
    return false;
  }
}

/** Light or dark: the operating system's choice unless the user picked one (kept in this browser only). */
export function useThemeChoice(): { choice: ThemeChoice; mode: 'light' | 'dark'; theme: Theme; setChoice: (c: ThemeChoice) => void } {
  const [choice, setChoiceState] = useState<ThemeChoice>(stored);
  const [dark, setDark] = useState(osDark);
  useEffect(() => {
    let mq: MediaQueryList | undefined;
    try {
      mq = window.matchMedia('(prefers-color-scheme: dark)');
    } catch {
      return undefined;
    }
    const on = () => setDark(mq!.matches);
    mq.addEventListener('change', on);
    return () => mq!.removeEventListener('change', on);
  }, []);
  const mode = choice === 'system' ? (dark ? 'dark' : 'light') : choice;
  const theme = mode === 'dark' ? darkTheme : lightTheme;
  useEffect(() => {
    document.documentElement.style.colorScheme = mode;
    document.body.style.background = theme.colorNeutralBackground2;
  }, [mode, theme]);
  const setChoice = (c: ThemeChoice) => {
    setChoiceState(c);
    try {
      if (c === 'system') window.localStorage.removeItem(KEY);
      else window.localStorage.setItem(KEY, c);
    } catch {
      // storage blocked: the choice lasts for this page only
    }
  };
  return { choice, mode, theme, setChoice };
}
