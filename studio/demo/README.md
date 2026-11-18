# Narrated demos

Two recorded demos of the studio, each a real browser session on a fresh workspace, driven click by
click, with a visible cursor, captions and a voice-over. The videos are in `docs/demo/`, each with
an `.srt` subtitle file.

| Video | What it shows |
| --- | --- |
| `01-concepts-and-walkthrough.mp4` | Tenants and roles, names, the catalogue, an activity, the corporate payment workflow, the canonical model and code sets, a scheme's dispatch table, the designer and the expanded form, the editors, a test and its traces, a whole payment end to end, the conformance kit, a bank's configuration, export, the audit log, the AI assistant's templates |
| `02-build-a-scheme-from-scratch.mp4` | The synthetic scheme `uae.demo` built in the web app: sample files, port and simulator, rule, rule set and file checks, the intake map and its script, maps and the send, the composite, its subflow and the leg, the scheme and its dispatch table published as one change set, the leg on the bench, the bank enabling the rail, a payment end to end, the export, and the deploy on DTFx |

## Record them again

The scripts are `demo/concepts.ts` and `demo/build.ts`; `demo/record.ts` drives the browser, lays the
voice at the time each scene starts, and joins them with ffmpeg. From `studio/`:

```bash
npx tsx demo/record.ts concepts
```

```bash
npx tsx demo/record.ts build
```

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
import sys, json, soundfile as sf
from kokoro_onnx import Kokoro
k = Kokoro(sys.argv[1], sys.argv[2])
samples, sr = k.create(sys.stdin.read(), voice=sys.argv[3], speed=1.0, lang="en-gb")
sf.write(sys.argv[4], samples, sr)
print(json.dumps({"seconds": len(samples) / sr}))
```

and point the recorder at it:

```bash
TH_TTS="tts/bin/python say.py kokoro-v1.0.int8.onnx voices-v1.0.bin bf_emma" npx tsx demo/record.ts build
```

Voice clips are cached in `docs/demo/.voice/` by their text, so a change to one line re-voices only that line.
