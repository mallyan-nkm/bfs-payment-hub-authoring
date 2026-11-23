import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { changedFields, fromInput, missing, stateLabel, toInput } from '../../src/registers/form';
import type { RegisterKind } from '../../src/registers/api';

// The registers area's form (R-05-62, R-05-63): from a kind's declaration alone, and a change sends only what the maker changed,
// so a masked field the console never sees in clear keeps the value the register holds.

const here = path.dirname(fileURLToPath(import.meta.url));
const decl = JSON.parse(fs.readFileSync(path.resolve(here, '..', '..', '..', 'src', 'TransactHub.Operations', 'Registers', 'corporate.register.json'), 'utf8'));
const kind: RegisterKind = { ...decl, fields: decl.fields.map((f: any) => ({ required: false, personal: false, lookup: false, ...f })) };

describe('the register form, from the kind\'s declaration', () => {
  it('reads the runtime\'s declaration of the corporate registration: its key, its lookups, its masked personal fields', () => {
    expect(kind.key).toBe('corporateId');
    expect(kind.fields.filter((f) => f.lookup).map((f) => f.id)).toEqual(['corporateId', 'accounts', 'cif', 'establishmentId']);
    expect(kind.fields.filter((f) => f.personal).every((f) => !!f.mask)).toBe(true);
  });

  it('edits a list as a comma list and a boolean as a checkbox', () => {
    const accounts = kind.fields.find((f) => f.id === 'accounts')!;
    expect(toInput(accounts, ['A1', 'A2'])).toBe('A1, A2');
    expect(fromInput(accounts, ' A1 , A2,, ')).toEqual(['A1', 'A2']);
    const active = kind.fields.find((f) => f.id === 'active')!;
    expect(toInput(active, true)).toBe(true);
    expect(fromInput(active, false)).toBe(false);
  });

  it('sends the key and only the fields the maker changed; a masked field left as shown is not sent', () => {
    const before = { corporateId: 'C-0001', name: 'Example Trading LLC', cif: 'CIF0****0111', active: true, accounts: ['AE07***************3456'] };
    const edited: Record<string, string | boolean> = Object.fromEntries(kind.fields.map((f) => [f.id, toInput(f, (before as any)[f.id])]));
    expect(changedFields(kind, before, edited)).toEqual({});
    edited.name = 'Example Trading Two LLC';
    edited.active = false;
    expect(changedFields(kind, before, edited)).toEqual({ corporateId: 'C-0001', name: 'Example Trading Two LLC', active: false });
  });

  it('asks for the required fields of a new record and sends what was given', () => {
    const edited: Record<string, string | boolean> = Object.fromEntries(kind.fields.map((f) => [f.id, toInput(f, undefined)]));
    expect(missing(kind, edited)).toEqual(['Corporate id', 'Corporate name']);
    edited.corporateId = 'C-0002';
    edited.name = 'Example Two LLC';
    edited.establishmentId = '0000007654321';
    expect(missing(kind, edited)).toEqual([]);
    expect(changedFields(kind, null, edited)).toEqual({ corporateId: 'C-0002', name: 'Example Two LLC', establishmentId: '0000007654321', active: false, wpsRegistrationFormHeld: false, labourMinistryRegistered: false });
    expect(stateLabel('pending')).toBe('Waiting for consent');
  });
});
