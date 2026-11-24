# Narrated demos and training videos

Three recorded videos of the studio (the north-star video and the two of the runtime are further down), each a real browser session on a fresh workspace, driven click by
click, with a visible cursor, captions and a voice-over. The videos are in `docs/demo/`, each with
an `.srt` subtitle file. The concept video and the hands-on training video (in `docs/demo/training/`) run at a slower pace,
in plain words for someone new to payments; the concept video draws every idea as a restaurant kitchen.

| Video | What it shows |
| --- | --- |
| `1-concept.mp4` | The concept, in two parts. First, with no screens, every idea drawn as a kitchen on the animated stage (`demo/explainer/ideas.html`, in the apps' navy and teal): the studio designs and the runtime runs; one order's journey; activities, outcomes and rules; values (dials with a default and bounds) beside logic that stays fixed; workflows, schemes, the slot and the leg; the factory (`p`, `uae.p`, `uae.wps`); a bank's hooks, its values and maker and checker; the distribution and the configuration release, and a payment pinned to its distribution; one bank per runtime; everything together. Then a short tour of the same ideas in the studio on Fluent UI 2: the top bar and the catalogue, the factory's scopes and the four-part names, an activity, a rule's editor, the corporate workflow on the canvas, the WPS dispatch table, a test with its simulator, the bank workspace, the configuration set, the audit log and the export |
| `training/training-2-hands-on.mp4` | Training 2, hands-on, one long file: a rule built in the form and tested by its examples, broken on purpose and fixed; an activity tested on the bench and saved as a test; the assistant beside the editor: help on a field, the rule in plain words, then a rule built by asking, allowed once, a change allowed always and undone; a WPS leg composed on the canvas from blocks that exist, with the new check; the order to build in when nothing exists; a bank's own rule with a parameter, its rule set, check and halt hook, the configuration refusing a value out of bounds, the compatibility run, and a salary file held for approval, declined and then approved; then the same payment in the debugger: stepping into and back, a breakpoint on the bank's hook point and on the route, the decision and the data it changed |
| `02-build-a-scheme-from-scratch.mp4` | The synthetic scheme `uae.demo` built in the web app: sample files, port and simulator, rule, rule set and file checks, the intake map and its script, maps and the send, the composite, its subflow and the leg, the scheme and its dispatch table published as one change set, the leg on the bench, the bank enabling the rail, a payment end to end, the export, and the deploy on DTFx |

## The north-star video

`3-north-star.mp4`, in `docs/demo/` with its `.srt`, voiced at the training pace (`TH_TTS_SPEED=0.85`, `TH_DEMO_CRF=29`, a little smaller than the concept video's 28, for its terminal and studio frames). It starts
with the architecture drawn as one diagram on the explainer stage (the studio and its GitHub branch; the runtime of one bank with
the File API and front door, the engine host, workers and native modules, the store, the read model, the consoles, ports and
adapters, machine auth and the bank's identity provider), then takes each north-star tenet in turn, comes back to the diagram to
light the parts it is about, and shows it working: ISO 20022 native (the studio's ISO data dictionary and proven samples, then the
store's tables, a salary file as a pain.001-shaped bulk and its payments, and the facts, which carry no personal data); the platform
workflow and the scheme leg (the slot, the designer's canvas, the WPS dispatch table); the workflow independent of the engine (the
export for Temporal and for Flowable, the kit on the studio interpreter, then the same eight scenarios run on DTFx, Elsa, Temporal and
Flowable, the waits cut); SQL Server and PostgreSQL; insert-only, versioned and hot update (the hash-chained facts, an update and a
delete refused to the runtime's login, two distributions side by side and draining, the effective-dated values, the outbox and the
event stream's position); a bank as configuration with the bank approver's consent; no-code and code, and signed native modules per
distribution; files, ports, circuit breakers, Kafka and MQTT; authentication and personas; one bank per runtime, the multi-tenant
studio on GitHub, and the laptop's compose file.

The tables are live: `demo/north-star/store.sh up` starts a PostgreSQL container of its own (`th-nstar-store` on loopback port 55433,
no compose project), applies the runtime's migrations, seeds through the store's own classes (`demo/north-star/store/Program.cs`) two
distributions of the example bank and one synthetic salary file of two lines with the facts of a run (written by the seeder, not
by an engine), and grants the runtime's login `th_runtime` read and insert only, as `deploy/laptop/sql/grants-postgres.sql` does.
Its password is made at random in that shell and never written down. The video reads the store with `psql` in the container. The
engines need what their kits need (`CLAUDE.md`: .NET 8, the Temporal test server's classpath, the Flowable harness). From `studio/`:

```bash
demo/north-star/store.sh up
TH_TTS_SPEED=0.85 TH_DEMO_CRF=29 npx tsx demo/run.ts north-star
demo/north-star/store.sh down
```

## The runtime videos

Two videos of the runtime with the studio, in `docs/demo/runtime/`, each with its `.srt`, voiced at the training pace
(`TH_TTS_SPEED=0.85`). Both run on the laptop stack (`deploy/laptop`). The drawn architecture video that was runtime video 2 is
replaced by the north-star video above.

| Video | What it shows |
| --- | --- |
| `runtime-1-install-and-screens.mp4` | Building the images, the stack's secrets by name only, `docker compose ... up -d --wait` and what runs; the studio's sign-in and its surfaces by role (designer, bank configurator, bank approver, auditor, administrator); the runtime's sign-in at the bank's development provider; every page of the service console (health, content, logs, errors and runbooks, actions, backup, assistant), the operations console (payments and a payment's page, work queue, approvals, reports, breaks, turnaround, retention, consents, registers, and its audit read from the API) and the test bench (run, test data, suite, history) |
| `runtime-3-full-payment-run.mp4` | One bank's payments end to end: its distribution installed and activated in the service console, the native modules it carries checked by hash, its configuration release (with the laptop's file transfer bindings) put in force, each on a bank approver's consent; a salary file uploaded on the operations console's upload page (`POST /ops/api/upload`, which posts to the File API's `POST /files/ui`), its SIF sent through the File API's outbox to the Central Bank's SFTP folder, the ACK left there and matched back, the payment settled; the synthetic scheme `uae.demo` built in the studio, licensed by the bank (its configurator proposes, its approver approves, I-205), distribution B activated while A drains and is removed, and a demo credit file paid on `uae.demo`; the fraud alerts, ledger accounts, rates badge and audit |

Bring the stack up first, with a compose project of your own and the secrets in your shell (`deploy/laptop/README.md`), then,
from `studio/`, with the same shell:

```bash
TH_TTS_SPEED=0.85 npx tsx demo/run.ts install --out ../docs/demo/runtime
```

```bash
TH_TTS_SPEED=0.85 npx tsx demo/run.ts full-run --out ../docs/demo/runtime
```

`demo/stack.ts` holds what the two stack videos share: the stack's URLs, the sign-in at the bank's development provider, the
services' APIs for a step that has no screen, and the commands shown in the terminal panel, run for real with every
`TH_LAPTOP_*` secret in their output replaced by stars. The full run starts on a fresh stack: it adds the bank's two studio
users and publishes the bank's first distribution before the camera, then installs it and everything after on screen. It needs the
demo scheme's simulator among the workers' simulators: bring the stack up with `TH_LAPTOP_WORKERS_BENCH` set to the default list in
`compose.yaml` plus `{"port":"uae.demo.port.demo.v1","simulator":"uae.demo.sim.demo-outward.v1","scenario":"accept"}`. Where a step has no screen today it is done as an operator would,
by the API or a command shown on screen, and the voice says so: the configuration release with the laptop's bindings is built with
the studio's API, the SIF and the ACK on the SFTP server are shown and left with `docker compose exec`, and a payment's kept files
are listed by the File API, since its page in the operations console does not show them yet (Issue I-286). A payment on Temporal
runs in real time; `Director.skip` cuts such a wait from the video and shows a card instead.

## Record them again

The scripts are `demo/concept.ts` (with its animated stage, `demo/explainer/ideas.html`), `demo/north-star.ts` (the same stage, and its store, `demo/north-star/`), `demo/build.ts`
and `demo/hands-on.ts` (with the documents it builds, in
`demo/hands-on/docs.ts`); `demo/record.ts` drives the browser, lays the
voice at the time each scene starts, and joins them with ffmpeg. From `studio/`:

```bash
TH_TTS_SPEED=0.85 TH_DEMO_CRF=28 npx tsx demo/run.ts concept
```

```bash
npx tsx demo/run.ts build
```

The hands-on training video goes to its own folder and was voiced a little slower:

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
hands-on production also checks its results as it goes. `TH_DEMO_CRF` sets the picture's quality (default 26; the concept
video used 28, a smaller file). `test/blackbox/demo-videos.test.ts` runs the three studio
productions this way, the north-star video where its store is up (one scenario on each engine), and the two stack videos where a laptop stack is up with its
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
