# Narrated demos and training videos

Four recorded videos of the studio, each a real browser session on a fresh workspace, driven click by
click, with a visible cursor, captions and a voice-over. The videos are in `docs/demo/`, each with
an `.srt` subtitle file. The two training videos are in `docs/demo/training/` and run at a slower pace,
in plain words for someone new to payments, with one picture throughout: a restaurant kitchen.

| Video | What it shows |
| --- | --- |
| `01-concepts-and-walkthrough.mp4` | Tenants and roles, names, the catalogue, an activity, the corporate payment workflow, the canonical model and code sets, a scheme's dispatch table, the designer and the expanded form, the editors, a test and its traces, a whole payment end to end, the conformance kit, a bank's configuration, export, the audit log, the AI assistant's templates |
| `training/training-1-the-ideas.mp4` | Training 1, about ten minutes, no screens: every idea of the studio drawn as a kitchen. Stations (activities), quality checks (rules, rule sets, decision tables, parameters), recipes (workflows, legs, subflows, composites), doors, stand-ins and translators (ports, simulators, maps), the standard ticket (the canonical context), partners (schemes, dispatch tables, slots), franchises (tenants, configuration, hooks), who may change what (maker and checker), tasting (tests and the bench), and service (export, workers, engines) |
| `training/training-2-hands-on.mp4` | Training 2, hands-on, one long file: a rule built in the form and tested by its examples, broken on purpose and fixed; an activity tested on the bench and saved as a test; a rule from the AI assistant's template; a WPS leg composed on the canvas from blocks that exist, with the new check; the order to build in when nothing exists; a bank's own rule with a parameter, its rule set, check and halt hook, the configuration refusing a value out of bounds, the compatibility run, and a salary file held for approval, declined and then approved |
| `02-build-a-scheme-from-scratch.mp4` | The synthetic scheme `uae.demo` built in the web app: sample files, port and simulator, rule, rule set and file checks, the intake map and its script, maps and the send, the composite, its subflow and the leg, the scheme and its dispatch table published as one change set, the leg on the bench, the bank enabling the rail, a payment end to end, the export, and the deploy on DTFx |

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

The hands-on video's AI chapter makes a live call only when the studio server has a model key
(`ANTHROPIC_API_KEY` in its environment); without one it shows the studio saying so, and the voice
says so too. Record it again once a key is set for the server to show a real draft.

`TH_DEMO_DRY=1` runs any production fast, with no video and no voice, as a check of every step; the
hands-on production also checks its results as it goes. `test/blackbox/demo-videos.test.ts` runs all
four this way.

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
