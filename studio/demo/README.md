# Narrated demos and training videos

Four recorded videos of the studio (the three of the runtime are further down), each a real browser session on a fresh workspace, driven click by
click, with a visible cursor, captions and a voice-over. The videos are in `docs/demo/`, each with
an `.srt` subtitle file. The two training videos are in `docs/demo/training/` and run at a slower pace,
in plain words for someone new to payments, with one picture throughout: a restaurant kitchen.

| Video | What it shows |
| --- | --- |
| `01-concepts-and-walkthrough.mp4` | Tenants and roles, names, the catalogue, an activity, the corporate payment workflow, the canonical model and code sets, a scheme's dispatch table, the designer and the expanded form, the editors, a test and its traces, a whole payment end to end, the conformance kit, a bank's configuration, export, the audit log, the AI assistant's templates |
| `training/training-1-the-ideas.mp4` | Training 1, about ten minutes, no screens: every idea of the studio drawn as a kitchen. Stations (activities), quality checks (rules, rule sets, decision tables, parameters), recipes (workflows, legs, subflows, composites), doors, stand-ins and translators (ports, simulators, maps), the standard ticket (the canonical context), partners (schemes, dispatch tables, slots), franchises (tenants, configuration, hooks), who may change what (maker and checker), tasting (tests and the bench), and service (export, workers, engines) |
| `training/training-2-hands-on.mp4` | Training 2, hands-on, one long file: a rule built in the form and tested by its examples, broken on purpose and fixed; an activity tested on the bench and saved as a test; the assistant beside the editor: help on a field, the rule in plain words, then a rule built by asking, allowed once, a change allowed always and undone; a WPS leg composed on the canvas from blocks that exist, with the new check; the order to build in when nothing exists; a bank's own rule with a parameter, its rule set, check and halt hook, the configuration refusing a value out of bounds, the compatibility run, and a salary file held for approval, declined and then approved; then the same payment in the debugger: stepping into and back, a breakpoint on the bank's hook point and on the route, the decision and the data it changed |
| `02-build-a-scheme-from-scratch.mp4` | The synthetic scheme `uae.demo` built in the web app: sample files, port and simulator, rule, rule set and file checks, the intake map and its script, maps and the send, the composite, its subflow and the leg, the scheme and its dispatch table published as one change set, the leg on the bench, the bank enabling the rail, a payment end to end, the export, and the deploy on DTFx |

## The runtime videos

Three videos of the runtime with the studio, in `docs/demo/runtime/`, each with its `.srt`, voiced at the training pace
(`TH_TTS_SPEED=0.85`). Videos 1 and 3 run on the laptop stack (`deploy/laptop`); video 2 is drawn, with no screens.

| Video | What it shows |
| --- | --- |
| `runtime-1-install-and-screens.mp4` | Building the images, the stack's secrets by name only, `docker compose ... up -d --wait` and what runs; the studio's sign-in and its surfaces by role (designer, bank configurator, bank approver, auditor, administrator); the runtime's sign-in at the bank's development provider; every page of the service console (health, content, logs, errors and runbooks, actions, backup, assistant), the operations console (payments and a payment's page, work queue, approvals, reports, breaks, turnaround, retention, consents, registers, and its audit read from the API) and the test bench (run, test data, suite, history) |
| `runtime-2-north-star-architecture.mp4` | The north-star architecture, drawn: ISO 20022 at the centre and scheme formats mapped at the edges; one insert-only store on SQL Server or PostgreSQL; four engines behind one contract, Temporal the default; the three layers and why; a payment pinned at acceptance, side by side, drained, removed, rolled back; platform workflows with slots, scheme legs (WPS also in a refund order), hooks and rules; the File API as the only door for files; the studio multi-tenant on its GitHub branch, a runtime one bank; maker-checker and the bank approver's consent; what is not built yet |
| `runtime-3-full-run.mp4` | Not recorded yet: its production (`demo/full-run.ts`) is written and dry-run. One master workflow across every console: a bank's values, hook and rule on the corporate workflow; the new scheme leg `uae.demo` connected by its dispatch table; the bench; maker-checker; install, consent, activate in the service console; a payment through the File API, followed in the operations console; a value changed by a configuration release; a second distribution, the first draining while a payment pinned to it finishes, then removed; a roll back |

Bring the stack up first, with a compose project of your own and the secrets in your shell (`deploy/laptop/README.md`), then,
from `studio/`, with the same shell:

```bash
TH_TTS_SPEED=0.85 npx tsx demo/run.ts install --out ../docs/demo/runtime
```

```bash
TH_TTS_SPEED=0.85 npx tsx demo/run.ts architecture --out ../docs/demo/runtime
```

```bash
TH_TTS_SPEED=0.85 npx tsx demo/run.ts full-run --out ../docs/demo/runtime
```

`demo/stack.ts` holds what the two stack videos share: the stack's URLs, the sign-in at the bank's development provider, the
services' APIs for a step that has no screen, and the commands shown in the terminal panel, run for real with every
`TH_LAPTOP_*` secret in their output replaced by stars. The full run starts on a fresh stack: it adds the bank's two studio
users, then publishes and installs the bank's distributions. Where a step has no screen today it is done as an operator would,
by the API or a command shown on screen, and the voice says so (Issues I-200 to I-203). A WPS payment on Temporal waits in real
time for its simulator's answer; `Director.skip` cuts such a wait from the video and shows a card instead.

## Record them again

The scripts are `demo/concepts.ts`, `demo/build.ts`, `demo/ideas.ts` (with its animated stage,
`demo/explainer/ideas.html`) and `demo/hands-on.ts` (with the documents it builds, in
`demo/hands-on/docs.ts`); `demo/record.ts` drives the browser, lays the
voice at the time each scene starts, and joins them with ffmpeg. From `studio/`:

```bash
npx tsx demo/run.ts concepts
```

```bash
npx tsx demo/run.ts build
```

The training videos go to their own folder and were voiced a little slower:

```bash
TH_TTS_SPEED=0.85 npx tsx demo/run.ts ideas --out ../docs/demo/training
```

```bash
TH_TTS_SPEED=0.85 npx tsx demo/run.ts hands-on --out ../docs/demo/training
```

The hands-on video's assistant chapter asks the live model only when the studio server has a model key
(`ANTHROPIC_API_KEY` or `TH_ANTHROPIC_API_KEY` in its environment). Without one, it shows **What is
happening**, which needs no model, and the **Ask** tab saying it has no credentials, and the voice
says so too. The recorded video was made with a key set, so it shows the agent at work. The server
takes its environment from the recorder, so a key set in the shell that runs the recorder reaches it.
The agent takes from a few seconds to a minute to answer; the recording waits up to four minutes
each time.

`TH_DEMO_DRY=1` runs any production fast, with no video and no voice, as a check of every step; the
hands-on production also checks its results as it goes. `test/blackbox/demo-videos.test.ts` runs the four studio
productions and the architecture video this way, and the two stack videos too where a laptop stack is up with its
secrets in the environment (the full run takes up to half an hour: it pays two payments on Temporal).

Needs ffmpeg, and for the build demo the .NET 8 SDK (it deploys on DTFx). Without a voice the videos
are recorded silent, with captions.

## The voice

Any text-to-speech command that reads text on stdin, writes a WAV to the path given as its last
argument, and prints `{"seconds": <length>}` works. The recorded voice is Kokoro (Apache-2.0), voice
`bf_emma`, run locally:

```bash
python -m venv tts && tts/bin/pip install kokoro-onnx soundfile
```

Download `kokoro-v1.0.int8.onnx` and `voices-v1.0.bin` from the `model-files-v1.0` release of
github.com/thewh1teagle/kokoro-onnx, save `say.py` beside them:

```python
import os, sys, json, soundfile as sf
from kokoro_onnx import Kokoro
k = Kokoro(sys.argv[1], sys.argv[2])
samples, sr = k.create(sys.stdin.read(), voice=sys.argv[3], speed=float(os.environ.get("TH_TTS_SPEED", "1")), lang="en-gb")
sf.write(sys.argv[4], samples, sr)
print(json.dumps({"seconds": len(samples) / sr}))
```

and point the recorder at it:

```bash
TH_TTS="tts/bin/python say.py kokoro-v1.0.int8.onnx voices-v1.0.bin bf_emma" npx tsx demo/run.ts build
```

Voice clips are cached in a `.voice/` folder beside the videos, by their text and speed, so a change to one line re-voices only that line.
