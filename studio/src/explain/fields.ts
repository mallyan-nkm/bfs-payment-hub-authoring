// Help for every field of the authoring forms, in plain words, for the "What is happening" panel
// (docs/ISSUES.md I-29). Keyed by the field's accessible label, which is what the panel sees when
// the designer moves into a field. A label built from a name (for example "Given of passes") is
// matched by its prefix. test/explain.test.ts fails when a form has a field without help here.

export interface FieldHelp {
  /** What the field means, in one or two sentences. */
  means: string;
  /** The values allowed, each with a line on what it means. */
  values?: [string, string][];
  example?: string;
  /** The mistake people make most often here. */
  mistake?: string;
}

const LEVELS: [string, string][] = [
  ['payment', 'One payment: a single credit transfer.'],
  ['batch', 'A whole file: its header, its count and its total.'],
  ['item', 'One line of a file. The step runs once for each line.'],
];
const DURATION = 'An ISO 8601 duration: PT30S is 30 seconds, PT5M is 5 minutes, P1D is one day.';

export const FIELD_HELP: Record<string, FieldHelp> = {
  // ------------------------------------------------------------ every document
  Name: {
    means: 'The unique name of this document. It is scope, type, short name and major version, joined by dots.',
    example: 'uae.wps.rule.salary-month-valid.v1',
    mistake: 'Never put a bank or tenant name in a shared name. A bank\'s own content uses the scope b.',
  },
  Release: { means: 'The release of this version: major.minor.patch. A fix is a patch, an improvement a minor, an interface change a new major (and a new name).', example: '1.0.0' },
  Title: { means: 'A short title in plain words. It shows in the catalogue and on the canvas.', example: 'The salary month is a real month' },
  Description: { means: 'A longer note on what this does and why. Optional.' },
  'Document YAML': { means: 'The whole document as YAML. The form and the YAML are the same document: change either one.', mistake: 'YAML reads on, off, yes and no as true or false. Never use them as keys.' },
  Kind: { means: 'What sort of document you are starting.', values: [['workflow', 'A recipe: steps that call blocks.'], ['hook', 'A bank\'s own small recipe, plugged into a hook point.']] },

  // ------------------------------------------------------------ rules
  'Rule kind': {
    means: 'What sort of check the rule is.',
    values: [
      ['structural', 'The shape of a file or message: records present and in order.'],
      ['field', 'One value on its own: present, a format, one of a list.'],
      ['cross-field', 'Values that must agree: a total and its lines, two dates.'],
      ['reference', 'A value that must exist somewhere else: a code, a bank, an account.'],
      ['policy', 'A bank or scheme policy, usually with a parameter: a limit, a cut-off.'],
      ['decision', 'Not a check: it yields a decision, such as which route to take.'],
    ],
  },
  'Rule level': { means: 'Where the value the rule looks at lives.', values: LEVELS, mistake: 'A file total is at batch level; each line\'s amount is at item level.' },
  Priority: { means: 'Which rule runs first when several match. Higher runs first. Leave it empty unless order matters.' },
  'Condition kind': {
    means: 'What this line of the When part does.',
    values: [
      ['match', 'Find a fact, such as the payment or the file, and give it a short name.'],
      ['exists', 'There is at least one fact like this.'],
      ['not', 'There is no fact like this.'],
      ['all', 'Every fact like this meets the condition.'],
      ['having', 'A test on names already matched.'],
      ['let', 'Work out a value once and name it.'],
      ['query', 'Collect facts, for example every line of a file, to count or add them.'],
      ['or', 'Any one of several groups holds.'],
      ['and', 'All of several groups hold.'],
    ],
  },
  Fact: {
    means: 'The kind of fact to find. Facts are parts of the canonical model.',
    values: [['Payment', 'One payment.'], ['Batch', 'A file\'s header, count and total.'], ['Item', 'One line of a file.'], ['Header', 'Routing details: kind, direction, scheme.'], ['Param', 'A value the bank sets.'], ['WpsBatch, WpsItem', 'The WPS parts of a salary file.']],
    example: 'WpsBatch',
  },
  'Binds as': { means: 'A short name for the fact, used in the conditions that follow.', example: 'w, so the condition can say w.salaryMonth' },
  Where: {
    means: 'A condition on the matched fact. When it is true, the rule has found what it looks for.',
    example: "not matches(w.salaryMonth, '^(0[1-9]|1[0-2])[0-9]{4}$')",
    mistake: 'The rule fires when the condition is TRUE. To reject a bad value, describe the bad value.',
  },
  Having: { means: 'A test on names matched above.', example: 'b.numberOfTransactions > maxLines.value' },
  'Yield kind': {
    means: 'What the rule produces when it fires.',
    values: [['finding', 'A problem with the payment: reject, repair or warn.'], ['decision', 'A decision with a value, for a decide activity.'], ['fact', 'A new fact for later rules.']],
  },
  Severity: {
    means: 'How serious the finding is.',
    values: [['reject', 'The payment or file is refused.'], ['repair', 'It can go on once someone fixes it.'], ['warn', 'Recorded, and the payment goes on.']],
  },
  'Finding path': { means: 'The path of the value that is wrong, in the canonical context. It tells people where to look.', example: 'ext.uae.wps.salaryMonth' },
  'Reason code': { means: 'A short standard ISO reason code for the finding. Every bank understands it.', example: 'DT01 is an invalid date, AM02 an amount above the limit, AM03 a currency not allowed.' },
  'Scheme code': { means: 'The scheme\'s own code for the same finding, if the scheme has one. Optional.' },
  'Decision name': { means: 'The name of the decision the rule yields.', example: 'route' },
  'Decision value': { means: 'The value of the decision.', example: 'standard' },
  'Decision reason': { means: 'A short reason for the decision, kept with the payment for audit.' },
  'Example name': { means: 'A short name for this example.', example: 'passes, fails, boundary at the limit' },
  'Evaluation date': { means: 'The date the example, test or run pretends it is. Values that change by date are looked up on it.', example: '2026-11-17' },
  'Given of ': {
    means: 'The made-up facts the example gives the rule, as JSON. Use the fact names from the When part.',
    example: '{ "WpsBatch": { "salaryMonth": "102026" } }',
    mistake: 'Only synthetic values. Never a real name, account or ID.',
  },
  'Expected reason codes of ': { means: 'The reason codes the rule must produce for this example. Leave it empty when the example must pass.', example: 'DT01' },
  'Expected decisions of ': { means: 'The decisions the rule or table must yield for this example, as JSON.', example: '[{ "name": "route", "value": "standard" }]' },

  // ------------------------------------------------------------ parameters
  'New parameter name': { means: 'The name of a value a bank may set, such as a limit. Starts with a small letter, no spaces.', example: 'maxLines' },
  'Parameter type': { means: 'The type of the value.', values: [['amount', 'Money: a number with a currency.'], ['integer', 'A whole number.'], ['decimal', 'A number with decimals.'], ['boolean', 'True or false.'], ['string', 'Text.'], ['duration', 'A length of time.'], ['code', 'A code from a code set.'], ['list', 'A list of values.']] },
  Default: { means: 'The value used when the bank has not set its own.', mistake: 'A value a bank may change is a parameter with a default and bounds, never a number written into a rule.' },
  Currency: { means: 'The currency of an amount parameter, three letters.', example: 'AED' },
  Minimum: { means: 'The lowest value a bank may set. A bank\'s configuration below it is refused.' },
  Maximum: { means: 'The highest value a bank may set.' },
  'Parameter description': { means: 'What the value controls, in plain words. Banks read this when they set it.' },

  // ------------------------------------------------------------ rule sets and decision tables
  Mode: {
    means: 'For a rule set: what its rules produce. For an exchange activity: how it talks to the other party.',
    values: [['validation', 'Findings: problems with the payment.'], ['decision', 'Decisions.'], ['send', 'Sends a message and expects a reply.'], ['respond', 'Answers a message that came in.'], ['inquire', 'Asks a question and waits for the answer.'], ['report', 'Sends a report; no reply is expected.'], ['receive', 'Waits for a message.']],
  },
  Stages: { means: 'The stages the rule set runs in, in order. A blocking finding in one stage stops the next stage.', example: 'structural, field, cross-field' },
  Rules: { means: 'The rules in this set. Type a published rule\'s name and press Enter.' },
  'Hit policy': { means: 'What happens when more than one row of the table matches.', values: [['unique', 'At most one row may match.'], ['first', 'The first matching row wins.'], ['collect', 'Every matching row counts.']] },
  'Input name': { means: 'A short name for one input column of the table.', example: 'kind' },
  'Input expression': { means: 'Where the input\'s value comes from in the context.', example: 'header.kind' },
  'Row ': { means: 'One cell of the table. Empty matches anything.' },

  // ------------------------------------------------------------ activities
  'Activity type': {
    means: 'What the station does.',
    values: [['validate', 'Checks the payment with a rule set.'], ['decide', 'Takes a decision with rules or a table.'], ['enrich', 'Adds a value, such as the business date.'], ['map', 'Translates between a scheme\'s format and the canonical model.'], ['exchange', 'Talks to another party through a port.'], ['post', 'Posts to accounts.'], ['screen', 'Sanctions or fraud screening.'], ['reserve', 'Reserves funds.'], ['hold', 'Parks the payment for a person.'], ['notify', 'Tells someone something happened.']],
  },
  'Activity level': { means: 'Whether the station works on a payment, a whole file, or each line.', values: LEVELS },
  'Invoked by': { means: 'Who starts the station.', values: [['workflow', 'A step of a workflow (the usual case).'], ['front-door', 'The front door, when a message arrives.'], ['schedule', 'A timer.']] },
  Effect: {
    means: 'What the station may change outside itself. It decides whether a retry is safe.',
    values: [['pure', 'Only looks. Changes nothing.'], ['read', 'Reads from outside, changes nothing.'], ['write', 'Changes something inside the bank.'], ['external', 'Changes something at another party.']],
    mistake: 'A write or external station may be retried only with an idempotency key.',
  },
  'Idempotency key': { means: 'A value that is the same every time the same request is sent, so a retry is not done twice.', example: "concat(header.paymentId, ':submit')" },
  Compensation: { means: 'The activity that undoes this one, if a later step fails and the workflow compensates.' },
  'Add a read': { means: 'A part of the context the station may read. It may read nothing else. Type a path and press Enter.', example: 'ext.uae.wps' },
  'Add a write': { means: 'A part of the context the station may change. It may change nothing else.', example: 'facts.validation' },
  'Primary ': { means: 'The normal way out. A workflow goes on to the next step on the primary outcome, and must say what to do on every other one.' },
  'Outcome name': { means: 'One way out of the station.', example: 'pass, reject, accepted, pending' },
  'Outcome description': { means: 'When this outcome happens, in plain words.' },
  'Error name': { means: 'A technical error the station may raise, as opposed to a business outcome.', example: 'TransientError' },
  'Retryable ': { means: 'Tick when the same request may simply be tried again after this error.' },
  'Error reason code': { means: 'The ISO reason code reported when this error ends the payment.' },
  'Error description': { means: 'When this error happens.' },
  'Add a port': { means: 'A connection to another party the station uses. On the bench: a port to bind to a simulator.' },
  'Add a rule set': { means: 'The rule set this validate or decide station runs. Type its name and press Enter.' },
  'Attempt timeout': { means: `How long one try may take. ${DURATION}` },
  'Total timeout': { means: `How long all tries together may take. ${DURATION}` },
  'Max attempts': { means: 'How many times to try in all, including the first.' },
  'First interval': { means: `How long to wait before the first retry. ${DURATION}` },
  Backoff: { means: 'How much longer each wait is than the one before. 2 doubles it.' },
  'Reply timeout': { means: `How long to wait for the other party's reply before the outcome is pending. ${DURATION}` },
  'Reply calendar': { means: 'Whether the reply time counts every hour, or business hours only.', values: [['elapsed', 'Every hour counts.'], ['business', 'Only business days count, by the scheme\'s calendar.']] },
  Binding: {
    means: 'What actually does the work behind the contract.',
    values: [['builtin', 'A service built into the platform, such as the rule service.'], ['script', 'A small TypeScript or Python script.'], ['native', 'A service the bank provides.'], ['subflow', 'A composite: a hidden small workflow that sends, waits and reads.']],
  },
  Service: { means: 'The built-in or native service that runs the station.', example: 'rule-service' },
  Language: { means: 'The language of the script-behind.', values: [['typescript', 'TypeScript.'], ['python', 'Python.']] },
  Script: { means: 'The file of the script-behind.', example: 'scripts/uae.demo.map.file-in.v1.ts' },
  'Script source': { means: 'The script itself. It may use only what the studio gives it: ctx, ports, rules, params, clock, ids and log.', mistake: 'No Date.now, no Math.random, no network and no files. The studio refuses them.' },
  Subflow: { means: 'The hidden small workflow behind a composite activity.' },
  'Map direction': { means: 'Which way the map translates.', values: [['in', 'From the scheme\'s format into the canonical model.'], ['out', 'From the canonical model into the scheme\'s format.']] },
  'Source model': { means: 'The scheme format the map reads or writes.', example: 'WPS SIF' },
  From: { means: 'Where a value comes from.', example: 'SCR.salaryMonth' },
  To: { means: 'Where the value goes.', example: 'ext.uae.wps.salaryMonth' },
  Via: { means: 'A function that converts the value on the way, if needed.', example: 'toDate' },

  // ------------------------------------------------------------ simulators
  Direction: {
    means: 'Which way messages go.',
    values: [['outward', 'The party we send to.'], ['inward', 'The party that sends to us.'], ['in', 'A message that comes in.'], ['out', 'A message that goes out.']],
  },
  Port: { means: 'The connection to one party: a scheme, core banking, screening. On the bench: the port to bind.' },
  'Stands for': { means: 'Who the simulator plays, in plain words.' },
  'State keyed by': { means: 'What the simulator remembers things by, for example a file id.' },
  'Scenario name': { means: 'A name for one script the simulator can play.', values: [['accept', 'Says yes.'], ['reject', 'Says no, with a reason.'], ['no-reply', 'Says nothing.'], ['late-reply', 'Answers after the time-out.']] },
  'Scenario description': { means: 'What happens in this scenario, in plain words.' },

  // ------------------------------------------------------------ workflows and the canvas
  'Start from': { means: 'How to start the new workflow.', values: [['Blank', 'An empty workflow of the kind you choose.'], ['From a template', 'A starter with typical steps.'], ['Clone a release', 'A copy of a published one, as a new release or a new name.']] },
  'Workflow kind': {
    means: 'What sort of workflow this is.',
    values: [['payment', 'The whole journey of a payment.'], ['leg', 'The scheme part of a payment. It fills a slot.'], ['segment', 'A shared piece used by several workflows.'], ['subflow', 'What a composite does underneath.'], ['handler', 'Handles a message that arrives, holds no state.']],
  },
  'Clone source': { means: 'The published workflow or hook to copy.' },
  'Published workflow': { means: 'Open a published workflow to read it or start its next release.' },
  'Insert a step here': { means: 'Adds a step at this place. Then choose its kind: call uses a station, forEach repeats for each line, switch branches, end finishes.' },
  'Search the catalogue': { means: 'Find a published block by its name, title or type.', example: 'salary-file' },
  'Step id': { means: 'A short name for the step, unique in the workflow. Traces and tests use it.', example: 'month-check' },
  Level: { means: 'On a step: the level it runs at, if not the block\'s own. On the bench: the level of the test.', values: [...LEVELS, ['leg, hook, end-to-end', 'On the bench: how much of a payment the run covers.']] },
  Action: {
    means: 'What the workflow does when a step ends with this outcome.',
    values: [['end the workflow', 'Stop here with the outcome you give.'], ['record and carry on', 'Note it and go on.'], ['run steps', 'Run the steps you add under it.'], ['return an outcome', 'In a subflow: give this outcome back to the caller.']],
    mistake: 'Every outcome other than the primary one must be handled.',
  },
  'End outcome': { means: 'How the workflow ends on this path.', example: 'rejected' },
  'Return outcome': { means: 'The outcome a subflow gives back to its caller.' },
  'For each in': { means: 'The list to repeat over.', example: 'items[] for each line of a file' },
  'On item failure': { means: 'What to do when one line fails.', values: [['stop', 'Stop at the first line that fails.'], ['continue', 'Note it and go on with the rest.']] },
  Concurrency: { means: 'How many lines may run at the same time.' },
  'Branch by': { means: 'What a switch looks at to choose a branch.' },
  'Outcome of step': { means: 'Branch on the outcome of this earlier step.' },
  Decision: { means: 'Branch on this decision.' },
  'Loop every': { means: `How long to wait between rounds of the loop. ${DURATION}` },
  'Loop max': { means: 'The most rounds the loop may run.' },
  'Until outcome of': { means: 'Stop the loop when this step ends with the outcome you give.' },
  'Wait duration': { means: `How long to wait. ${DURATION}` },
  'Awaited message': { means: 'The message a subflow waits for.', example: 'uae.wps.ack' },
  'Correlate by': { means: 'How the reply is matched to what was sent.', example: 'header.fileId' },
  'Await timeout': { means: 'How long to wait for the message. Empty takes the calling step\'s reply time.' },
  Calendar: { means: 'Which calendar the time counts on.' },
  'Deadline after': { means: `When the deadline lane runs if the workflow has not finished. ${DURATION}` },
  Slot: { means: 'A place in the workflow that a scheme\'s leg fills.', example: 'scheme-leg' },
  'Slot name': { means: 'The name of a slot this workflow declares.' },
  'Primary outcome': { means: 'The normal way out of the slot.' },
  'Hook point': { means: 'A marked place where a bank may plug in its own small recipe.' },
  'Hook point name': { means: 'The name of a hook point this workflow offers to banks.', example: 'after-validate' },
  'Hook point type': { means: 'What a hook at this point may do.', values: [['notify', 'Only tell someone. Cannot stop the payment.'], ['transform', 'Add the bank\'s own values.'], ['halt', 'Hold or reject the payment.']] },
  'Implements activity': { means: 'The composite activity this subflow is the inside of.' },
  'subflow draft': { means: 'Saves the subflow behind this composite as a draft.' },
  Signal: { means: 'On the bench: a signal sent during the run, such as pause, resume or cancel.' },

  // ------------------------------------------------------------ bench and tests
  Target: { means: 'What the bench runs: a published workflow, hook or activity. On the assistant: the activity or port a draft is for.' },
  'Run tenant': { means: 'Which tenant the run acts for. A bank\'s configuration and hooks apply when you choose its tenant.' },
  Simulator: { means: 'The stand-in that plays the party behind the port.' },
  Scenario: { means: 'Which script the simulator plays this time: accept, reject, no reply, a late reply, and so on.' },
  'Context JSON': { means: 'The made-up payment to run with, as JSON in the canonical model.', mistake: 'Only synthetic values.' },
  'Inbound sample': { means: 'A sample file or message to run with, as if it arrived from the bank channel or a scheme.' },
  'Start message': { means: 'For a whole payment: the scheme message the sample is. The scheme\'s dispatch table says which workflow it starts.' },
  'Expectation JSON': { means: 'What the run must show, as JSON. Optional.', example: '{ "outcome": "settled" }' },
  'Load from test': { means: 'Fill the bench from a saved test.' },
  'Run with my drafts': { means: 'A draft run: the target and every call come from your drafts first, then from what is published. The run is marked as a draft run and publishes nothing.', mistake: 'Only published releases go into a distribution or an export: approve the change set to publish.' },
  'Test id': { means: 'A unique id for the test.', example: 'T-TRAIN-MONTH-13' },
  'Test title': { means: 'What the test shows, in plain words.' },
  'Expected outcome': { means: 'The outcome the run must end with.' },
  'Expected reason code': { means: 'The reason code the run must end with.' },
  'Expected virtual time': { means: 'How long the run must take on the virtual clock.' },
  'Expected attempts JSON': { means: 'How many tries each step must take, as JSON.', example: '{ "send": 3 }' },
  'Add a trace entry': { means: 'A step and its outcome the run must show, as step:outcome.', example: 'month-check:reject' },
  'Add a detail entry': { means: 'A step inside a composite and its outcome.', example: 'submit.send:accepted' },
  'Sample file name': { means: 'The name of the sample file to save.', example: 'demo-credit-file-valid.csv' },
  'Sample content': { means: 'The content of the sample file.', mistake: 'Only synthetic values.' },

  // ------------------------------------------------------------ tenant
  Bank: { means: 'The bank whose configuration you are looking at.' },
  'Configuration set YAML': {
    means: 'The bank\'s configuration: the shared releases it accepts, the values it sets, and the hooks it binds, each from a date.',
    mistake: 'A value outside its bounds, or two values for the same dates, is refused (check L14).',
  },
  'Save as': { means: 'Which of the bank\'s people saves the new configuration release.' },
  'Candidate releases YAML': { means: 'New shared releases to test against, before the bank accepts them. Empty tests the current ones.' },
};

/** Labels that are actions, not fields: they need no help. */
export const NO_HELP = ['Move up', 'Move row up', 'Filter parameters', 'Filter tests', 'Cut ', 'Delete ', 'Remove ', 'Move ', 'Count of ', 'Open a published ', 'Debug ', 'Debugger controls', 'Workflow steps', 'Breakpoint at ', 'Picture'];

export function fieldHelp(label: string | undefined): (FieldHelp & { label: string }) | undefined {
  if (!label) return undefined;
  const exact = FIELD_HELP[label];
  if (exact) return { label, ...exact };
  const prefix = Object.keys(FIELD_HELP).filter((k) => k.endsWith(' ') && label.startsWith(k)).sort((a, b) => b.length - a.length)[0];
  return prefix ? { label, ...FIELD_HELP[prefix] } : undefined;
}
