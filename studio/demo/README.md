# Narrated demos and training videos

Four recorded videos of the studio and the laptop stack (the north-star video and the runtime's remaining one are further down), each a real browser session on a fresh workspace, driven click by
click, with a visible cursor, captions and a voice-over. The videos are in `docs/demo/`, each with
an `.srt` subtitle file. They run at a slow pace, in plain words for someone new to payments; the concept video draws every idea
as a restaurant kitchen. The two console videos, 5a and 5b, show every feature of the studio for one kind of user each; the
service console's video is recorded on the laptop stack.

| Video | What it shows |
| --- | --- |
| `1-concept.mp4` | The concept, in two parts. First, with no screens, every idea drawn as a kitchen on the animated stage (`demo/explainer/ideas.html`, in the apps' navy and teal): the studio designs and the runtime runs; one order's journey; activities, outcomes and rules; values (dials with a default and bounds) beside logic that stays fixed; workflows, schemes, the slot and the leg; the factory (`p`, `uae.p`, `uae.wps`); a bank's hooks, its values and maker and checker; the distribution and the configuration release, and a payment pinned to its distribution; one bank per runtime; everything together. Then a short tour of the same ideas in the studio on Fluent UI 2: the top bar and the catalogue, the factory's scopes and the four-part names, an activity, a rule's editor, the corporate workflow on the canvas, the WPS dispatch table, a test with its simulator, the bank workspace, the configuration set, the audit log and the export |
| `2-installation.mp4` | The installation, on the laptop stack (`deploy/laptop`) on Fluent UI 2: what you need (Docker, and the secrets made at random in your shell, never in a file) and why; the images built and the stack started (the SFTP server first and its host keys pinned, then `up -d --wait`), what each service is (the studio, the runtime's APIs and consoles, the engine host, the workers, the native host, the File API, machine auth, the bank's development provider, SQL Server, Temporal, SFTP; the engine profiles, and the `events` profile's brokers) and the health check; the first sign-in to the studio, the operations console, the registers, the service console and the test bench, light and dark; the bank's first distribution installed and activated, and its configuration release with the laptop's bindings put in force, each with a bank approver's consent; a salary file on the test bench, on the reference interpreter with the bank's values (its run on Temporal on the laptop is Issue I-331), then one real payment: uploaded on the operations console, its SIF delivered to the Central Bank's folder on the SFTP server, the ACK left there and read back, the payment settled; then stop, reset (`down -v`) and the same stack started on PostgreSQL |
| `5a-bank-configurator.mp4` | One video per console: the studio as a bank's configurator and its approver (`demo/bank-configurator.ts`). Before the camera the platform publishes the synthetic scheme `uae.demo` through the API (`test/blackbox/demo-scheme.ts`). Then: the bank workspace; `uae.demo` licensed, the configurator proposing and the approver approving; the configuration set and its dials with bounds and dates, a value out of bounds refused (L14); the bank's rule with its parameter and examples, and the rail-choice decision table; reference tables, a new edition of the cut-offs and a new table started in the editor's New table form; port bindings with the rates port bound by a secret reference, the rate feed and the standing consent to its releases (told, not shown: the service console has its own video); the bank's hooks at the corporate workflow's hook points and the compatibility run; maker and checker, the change set and the approvals in Governance (the new table first, I-340); a demo credit file paid on the bench with the bank's configuration; the audit log verified; what reaches the runtime |
| `5b-studio-designer-developer.mp4` | One video per console: the studio as the platform's designer and developer (`demo/designer.ts`, with the documents it builds in `demo/designer/docs.ts`). The catalogue and the factory's scopes and names; the ISO data dictionary (readers and writers, the canonical model, the code registry, the samples); a rule written in the form with its examples, broken and fixed, and the decision table editor; the corporate workflow on the canvas, its outline and expanded form, a composite and its subflow, the WPS leg in its slot; the WPS dispatch table; a port and its simulator's standard scenarios; then a new activity that needs a script, `uae.wps.validate.pay-period.v1`, written in full: its contract in the form, its TypeScript script-behind typed in the editor (a first try that reads the clock is refused), published, run on the bench, its two runs saved as tests, approved and run as stored tests, the activity used in a leg on the canvas, and the leg on the bench with a good and a bad sample and in the debugger; the explain panel and the assistant (Ask without a model key says so); import of the training bundle; the export to Temporal, Flowable, Elsa and DTFx; the conformance kit; governance and the GitHub branch |

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

One video of the runtime with the studio, in `docs/demo/runtime/`, with its `.srt`, voiced at the training pace
(`TH_TTS_SPEED=0.85`). It runs on the laptop stack (`deploy/laptop`), as do the installation video and the north-star video's store above.

| Video | What it shows |
| --- | --- |
| `runtime-3-full-payment-run.mp4` | One bank's payments end to end: its distribution installed and activated in the service console, the native modules it carries checked by hash, its configuration release (with the laptop's file transfer bindings) put in force, each on a bank approver's consent; a salary file uploaded on the operations console's upload page (`POST /ops/api/upload`, which posts to the File API's `POST /files/ui`), its SIF sent through the File API's outbox to the Central Bank's SFTP folder, the ACK left there and matched back, the payment settled; the synthetic scheme `uae.demo` built in the studio, licensed by the bank (its configurator proposes, its approver approves, I-205), distribution B activated while A drains and is removed, and a demo credit file paid on `uae.demo`; the fraud alerts, ledger accounts, rates badge and audit |

Bring the stack up first, with a compose project of your own and the secrets in your shell (`deploy/laptop/README.md`), then,
from `studio/`, with the same shell:

```bash
TH_TTS_SPEED=0.85 TH_DEMO_CRF=28 TH_DEMO_BUILD_LOG=<the log of build.sh> npx tsx demo/run.ts install
```

```bash
TH_TTS_SPEED=0.85 npx tsx demo/run.ts full-run --out ../docs/demo/runtime
```

`demo/stack.ts` holds what the two stack videos share: the stack's URLs, the sign-in at the bank's development provider, the
services' APIs for a step that has no screen, and the commands shown in the terminal panel, run for real with every
`TH_LAPTOP_*` secret in their output replaced by stars. The installation video starts on a fresh stack, adds the bank's two studio users and
publishes the bank's first distribution before the camera, and shows the last lines of the build's log named by `TH_DEMO_BUILD_LOG`
(the build is not run while recording). Its last chapter stops the stack, removes its volumes and starts it again on PostgreSQL, for
real, when recording; a dry run leaves the stack as it is unless `TH_DEMO_RESET=1`. The full run starts on a fresh stack: it adds the bank's two studio
users and publishes the bank's first distribution before the camera, then installs it and everything after on screen. It needs the
demo scheme's simulator among the workers' simulators: bring the stack up with `TH_LAPTOP_WORKERS_BENCH` set to the default list in
`compose.yaml` plus `{"port":"uae.demo.port.demo.v1","simulator":"uae.demo.sim.demo-outward.v1","scenario":"accept"}`. Where a step has no screen today it is done as an operator would,
by the API or a command shown on screen, and the voice says so: the configuration release with the laptop's bindings is built with
the studio's API, the SIF and the ACK on the SFTP server are shown and left with `docker compose exec`, and a payment's kept files
are listed by the File API, since its page in the operations console does not show them yet (Issue I-286). A payment on Temporal
runs in real time; `Director.skip` cuts such a wait from the video and shows a card instead.

## Record them again

The scripts are `demo/concept.ts` (with its animated stage, `demo/explainer/ideas.html`), `demo/north-star.ts` (the same stage,
and its store, `demo/north-star/`), `demo/bank-configurator.ts` and `demo/designer.ts` (with the documents it builds, in
`demo/designer/docs.ts`); `demo/record.ts` drives the browser, lays the voice at the time each scene starts, and joins them with
ffmpeg. Each runs the studio's own server on an in-memory fake GitHub seeded from `content-configurations/`, as
`npm run studio:dev` does. From `studio/`:

```bash
TH_TTS_SPEED=0.85 TH_DEMO_CRF=28 npx tsx demo/run.ts concept
TH_TTS_SPEED=0.85 TH_DEMO_CRF=28 npx tsx demo/run.ts bank-configurator
TH_TTS_SPEED=0.85 TH_DEMO_CRF=28 npx tsx demo/run.ts designer
```

The designer video's assistant scene shows **What is happening**, which needs no model, and the **Ask** tab saying it has no
credentials, as the studio server had no model key (`ANTHROPIC_API_KEY` or `TH_ANTHROPIC_API_KEY`) when it was recorded. With a
key in the recorder's environment the server takes it, and that scene is left out.

`TH_DEMO_SHOTS=<dir>` saves a screenshot of the page at the end of every scene, to look a run over before recording it.

`TH_DEMO_DRY=1` runs any production fast, with no video and no voice, as a check of every step; the
console productions also check their results as they go. `TH_DEMO_CRF` sets the picture's quality (default 26; the concept
video used 28, a smaller file). `test/blackbox/demo-videos.test.ts` runs the three studio
productions this way, the north-star video where its store is up (one scenario on each engine), and the two stack videos where a laptop stack is up with its
secrets in the environment (the full run takes up to half an hour: it pays two payments on Temporal).

Needs ffmpeg. Without a voice the videos
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
TH_TTS="tts/bin/python say.py kokoro-v1.0.int8.onnx voices-v1.0.bin bf_emma" npx tsx demo/run.ts designer
```

Voice clips are cached in a `.voice/` folder beside the videos, by their text and speed, so a change to one line re-voices only that line.
