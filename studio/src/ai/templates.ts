import type { DraftKind } from './assistant.js';

// Prompt templates for the AI assistant (C16): a starting instruction per kind of draft, written in
// the studio's terms, with the parts the designer must supply in <angle brackets>. A template only
// fills the instruction; the draft still runs through every gate and maker-checker.

export interface PromptTemplate {
  id: string;
  kind: DraftKind;
  title: string;
  use: string; // when to choose it
  instruction: string;
  sourceHint: string; // what to paste as source text
  language?: 'typescript' | 'python';
}

const ASK = 'Cite the clause each part comes from. Anything the source text does not say, ask as a question; do not guess.';

export const TEMPLATES: PromptTemplate[] = [
  // ------------------------------------------------------------------ rules
  {
    id: 'rule-field', kind: 'rule', title: 'Field check',
    use: 'One value on its own: present, a format, one of a list.',
    instruction: `Write a field rule named <scope>.rule.<name>.v1 at level <payment | batch | item>. It checks that <path, for example payment.purpose> <condition, for example is present and is one of SALA, PENS>. When it does not hold, yield a <reject | repair | warn> finding on that path with ISO reason code <code, for example AM03>. Include a passing example and a failing example. ${ASK}`,
    sourceHint: 'Paste the public clause that defines this field and its allowed values.',
  },
  {
    id: 'rule-cross', kind: 'rule', title: 'Cross-field or batch total',
    use: 'Values that must agree: a total and its lines, a count and its records, two dates.',
    instruction: `Write a cross-field rule named <scope>.rule.<name>.v1 at level <batch | payment>. It checks that <relation, for example the batch control sum equals the sum of the item amounts>. Use a query over the Item facts where it needs the lines. When it does not hold, yield a reject finding on <path> with ISO reason code <code>. Include a passing example, a failing example, and a boundary example. ${ASK}`,
    sourceHint: 'Paste the clause that states how the values must agree.',
  },
  {
    id: 'rule-limit', kind: 'rule', title: 'Limit with a bank parameter',
    use: 'A threshold a bank sets in its configuration, within bounds.',
    instruction: `Write a policy rule named <scope>.rule.<name>.v1. It compares <amount path, for example payment.instructedAmount.value> with a parameter <parameterName> of type amount, default <value>, bounds <min and max, or none>, which a bank sets in its configuration: a value, never a constant. It applies to <a payment | each line of a file | the file total | all three>. Above the parameter, yield a reject finding with ISO reason code <code>. Include a passing example, a failing example, and a boundary example at exactly the limit. ${ASK}`,
    sourceHint: 'Paste the clause that sets the limit, and say whether a bank may lower or raise it.',
  },
  {
    id: 'rule-decision', kind: 'rule', title: 'Decision',
    use: 'A choice a workflow reads as a decision: a route, a priority, a queue.',
    instruction: `Write a decision rule named <scope>.rule.<name>.v1. When <condition>, yield the decision <decisionName> with the value <value> and a reason. Include an example for each value it can yield. ${ASK}`,
    sourceHint: 'Paste the text that says how the choice is made.',
  },
  // ------------------------------------------------------------------ activities
  {
    id: 'activity-validate', kind: 'activity', title: 'Validate with a rule set',
    use: 'A check step that runs rules and reports findings.',
    instruction: `Write a validate activity contract named <scope>.validate.<name>.v1 at level <payment | batch | item>. It reads <paths> and writes facts.validation. It runs the rule set <rule set name>. Outcomes: pass (primary) and <reject, repair>. Effect pure; binding builtin, service rule-service. ${ASK}`,
    sourceHint: 'Paste the clauses the checks come from.',
  },
  {
    id: 'activity-exchange', kind: 'activity', title: 'Send and wait for a reply (composite)',
    use: 'Talking to a scheme or party that answers later.',
    instruction: `Write a composite exchange activity named <scope>.exchange.<name>.v1, mode send. It sends <message id> on the port <port name> and waits for <reply message id>, correlated by <key, for example header.fileId>. Outcomes: accepted (primary), rejected, pending. Reply timeout <PT30M | P1D> on the <business | calendar> calendar. Binding subflow <scope>.sub.<name>.v1. It carries an idempotency key. ${ASK}`,
    sourceHint: 'Paste the scheme text on the message, the reply and how long the party may take.',
  },
  {
    id: 'activity-map-in', kind: 'activity', title: 'Inward map from a file or message',
    use: 'Reading a scheme or channel format into the canonical context.',
    instruction: `Write an inward map activity named <scope>.map.<name>-in.v1 from <source format, for example a CSV file with H and D records>. It reads messages and writes <header, batch, items[]>. Outcomes: mapped (primary) and unmappable. Map direction in, source model <name>. Binding script, language <typescript | python>. It reads the file only; the dispatch table says what payment it starts. ${ASK}`,
    sourceHint: 'Paste the public description of the format, or a synthetic sample.',
  },
  {
    id: 'activity-enrich', kind: 'activity', title: 'Enrich from a port',
    use: 'Looking something up from a bank or scheme service.',
    instruction: `Write an enrich activity named <scope>.enrich.<name>.v1. It asks the port <port name> for <what> and writes <path>. Outcomes: <found (primary), not-found>. Effect read. Retry on TransientError, at most <3> attempts; attempt timeout <PT10S>. ${ASK}`,
    sourceHint: 'Paste what the service answers and when it may not know.',
  },
  // ------------------------------------------------------------------ scripts
  {
    id: 'script-parse', kind: 'script', title: 'Parse a file (inward map)', language: 'typescript',
    use: 'The script behind an inward map.',
    instruction: `Write the script-behind for the chosen map. The file is in ctx.messages.inbound.payload, in this format: <records and fields, or see the sample in the source text>. Write <ctx.header fields, ctx.batch, ctx.items>; keep what the header already holds. Return 'mapped', or { outcome: 'unmappable', reasonCode: 'FF01' } when <conditions, for example no header record or no lines>. Do not set the payment's direction or segment: the dispatch table routes. Use only the injected interface: no clock, randomness, files or network.`,
    sourceHint: 'Paste a synthetic sample file. Never real names or account numbers.',
  },
  {
    id: 'script-port', kind: 'script', title: 'Call a port and decide', language: 'typescript',
    use: 'The script behind an enrich or exchange that needs real logic.',
    instruction: `Write the script-behind for the chosen activity. Call ports.request('<port name>') with <request fields>. From the reply, write <path>. Return '<outcome>' when <condition>, otherwise '<other outcome>'. Use only the injected interface: no clock, randomness, files or network.`,
    sourceHint: 'Paste what the port returns, with a synthetic example.',
  },
  // ------------------------------------------------------------------ simulators
  {
    id: 'sim-outward', kind: 'simulator', title: 'A party that answers the bank',
    use: 'A scheme or service the bank sends to.',
    instruction: `Write an outward simulator for the chosen port, standing for <party>. It expects <request message id> and replies with <reply message id>. Include the eight standard scenarios: accept, reject with reason code <code>, pending-then-final, no-reply, late-reply, duplicate-reply, fail-twice-then-accept, malformed-reply. Add <any scheme-specific scenarios, or none>. Keep conversation state keyed by message id. ${ASK}`,
    sourceHint: 'Paste the scheme text on its replies and reason codes.',
  },
  {
    id: 'sim-inward', kind: 'simulator', title: 'A party that sends to the bank',
    use: 'A scheme that starts payments by sending a file or message.',
    instruction: `Write an inward simulator for the chosen port, standing for <party>. It starts by sending <start message id>, then expects <acknowledgement message id> from the bank. Add scenarios for <a valid file, a malformed file, a duplicate>. ${ASK}`,
    sourceHint: 'Paste the scheme text on the message it sends.',
  },
];

/**
 * The parts of an instruction still to fill: text in <angle brackets> that touches both brackets.
 * A comparison such as "amount < 10 and total > 5" has spaces inside, so it is not a blank.
 */
export const BLANK = /<(?=\S)([^<>\n]{1,80})(?<=\S)>/g;
export function unfilled(instruction: string): string[] {
  return [...instruction.matchAll(BLANK)].map((m) => m[1]);
}
