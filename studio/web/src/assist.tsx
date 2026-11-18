import { useEffect, useRef, useSyncExternalStore } from 'react';

// What the surface on screen offers the assistant: which surface it is, the document as it stands,
// and how to change it. A surface registers while it is mounted; the dock reads it (I-29).

export type Surface = 'editor' | 'designer' | 'tenant' | 'bench';

export interface AssistReg {
  surface: Surface;
  kind?: string;
  doc?: any;
  setDoc?: (doc: any) => void;
  selected?: string;
  report?: any;
  /** The tenant the surface acts for, when it is not the studio's (a bank's configuration page). */
  tenant?: string;
  /** A short name for the conversation: a new document starts a new one. */
  topic?: string;
}

let current: AssistReg | undefined;
const listeners = new Set<() => void>();
const emit = () => listeners.forEach((l) => l());

export const assistBus = {
  get: () => current,
  subscribe: (l: () => void) => {
    listeners.add(l);
    return () => listeners.delete(l);
  },
};

export function useAssistReg(): AssistReg | undefined {
  return useSyncExternalStore(assistBus.subscribe, assistBus.get, assistBus.get);
}

/** Offer this surface to the assistant while it is on screen. */
export function useAssist(reg: AssistReg) {
  const setter = useRef(reg.setDoc);
  setter.current = reg.setDoc;
  const sig = JSON.stringify({ ...reg, setDoc: undefined });
  useEffect(() => {
    current = { ...reg, setDoc: (d: any) => setter.current?.(d) };
    emit();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sig]);
  useEffect(() => () => {
    current = undefined;
    emit();
  }, []);
}
