import YAML from 'yaml';

// YAML loading. The spec's own tools read YAML with PyYAML, where a digit string with a
// leading zero (a person id, a routing code) is a string, not a number. The YAML 1.2 core
// schema would read it as a number and lose the zeros, so the integer tag is narrowed to
// match. Dates stay strings, as in the canonical JSON the content hash is taken over.

const customTags = (tags: any[]) =>
  tags.map((t) => (t.tag === 'tag:yaml.org,2002:int' && !t.format ? { ...t, test: /^[-+]?(?:0|[1-9][0-9_]*)$/ } : t));

export const YAML_OPTIONS = { customTags, merge: true } as const;

export function parseYaml(text: string): any {
  return YAML.parse(text, YAML_OPTIONS as any);
}

export function parseYamlAll(text: string): any[] {
  return YAML.parseAllDocuments(text, YAML_OPTIONS as any).map((d) => d.toJSON());
}

/**
 * A document as text. Never with anchors and aliases: an object a document holds twice is written out twice,
 * so the text depends on the content only, not on which objects in memory happen to be shared. A draft and
 * its published file are then the same text for the same content, as the draft listing expects.
 */
export function toYaml(doc: any): string {
  return YAML.stringify(doc, { lineWidth: 0, aliasDuplicateObjects: false });
}
