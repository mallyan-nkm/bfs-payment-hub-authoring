#!/usr/bin/env bash
# Builds the laptop stack's images (deploy/laptop/README.md). From the repository root:
#
#   deploy/laptop/build.sh            from source, inside Docker (the SDK, Node and Go build stages of the Dockerfile)
#   deploy/laptop/build.sh --local    with this machine's .NET 10 SDK, Node 22 and Go: the outputs in deploy/laptop/.build/,
#                                     copied into the same final stages (for a machine whose Docker builds have no network)
#
# Images, all tagged th-laptop-*:${TH_LAPTOP_TAG:-dev}: th-laptop-studio, th-laptop-runtime, th-laptop-temporal. The studio and the
# runtime share their base and the studio bundle's layer (the test bench runs the studio's deploy runner), so the second costs
# only the runtime's own layers on disk.
set -euo pipefail
cd "$(dirname "$0")/../.."
TAG=${TH_LAPTOP_TAG:-dev}
OUT=deploy/laptop/.build
MODE=source
[ "${1:-}" = "--local" ] && MODE=local

if [ "$MODE" = local ]; then
  rm -rf "$OUT/opt" "$OUT/studio-deps"
  mkdir -p "$OUT/opt/th" "$OUT/temporal"
  export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
  # The runtime's hosts and tools, each where the repository's layout puts its build (the deploy runner and the test bench find
  # the harnesses there, and run them; nothing is built in the image).
  for p in src/TransactHub.Api src/TransactHub.Service.Api src/TransactHub.Operations.Api src/TransactHub.Native src/TransactHub.FileApi \
           tools/TransactHub.TestBench tools/TransactHub.EngineHost tools/TransactHub.TemporalHarness tools/TransactHub.DtfxHarness \
           tools/TransactHub.ElsaHarness tools/TransactHub.FlowableHarness tools/TransactHub.DevIdentity tools/TransactHub.Migrate; do
    dotnet publish "runtime/$p" -c Release --nologo -v q $( [ "$p" = tools/TransactHub.TemporalHarness ] && echo "-r linux-x64 --self-contained false" ) -o "$OUT/opt/th/runtime/$p/bin/Release/net10.0" -p:UseAppHost=false -p:DebugType=none
    # The publish leaves a Release build in the project too; the image needs only the published copy.
    rm -rf "runtime/$p/bin/Release" "runtime/$p/obj/Release"
  done
  sh deploy/laptop/dedupe.sh "$OUT/opt/th/runtime"
  # The platform's and the schemes' native modules for the studio image: a distribution carries them; the runtime image never
  # does (R-09-120, R-09-126). Signed by a key made for this build and held in the environment only (R-09-127); the studio image
  # trusts that key, by its public key in native-module-signers.json.
  rm -rf "$OUT/native-modules"
  TH_LAPTOP_MODULE_KEY="$(openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256)" TH_NATIVE_SIGNING_KEY=env:TH_LAPTOP_MODULE_KEY sh -c '
    sh runtime/tools/native-module/build.sh p runtime/src/TransactHub.Activities.Platform.Native "$0/native-modules"
    sh runtime/tools/native-module/build.sh uae.wps runtime/src/TransactHub.Activities.Wps "$0/native-modules"
    sh runtime/tools/native-module/build.sh --signers > "$0/native-module-signers.json"' "$OUT"
  # The runtime's web app, built.
  (cd runtime/ops-console && npm run build --silent)
  mkdir -p "$OUT/opt/th/runtime/ops-console" && cp -r runtime/ops-console/dist "$OUT/opt/th/runtime/ops-console/dist"
  # The studio's web app, built, and its production packages only.
  (cd studio && npm run web:build --silent)
  mkdir -p "$OUT/studio-deps" && cp studio/package.json studio/package-lock.json "$OUT/studio-deps/"
  (cd "$OUT/studio-deps" && npm ci --omit=dev --no-audit --no-fund --silent)
  # Temporal with the SQL Server plugin, static.
  if [ ! -x "$OUT/temporal/temporal-server" ]; then
    (cd runtime/temporal-mssql && CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o "../../$OUT/temporal/" ./cmd/...)
  fi
  ARGS=(--build-arg DOTNET_FROM=dotnet-local --build-arg STUDIO_FROM=studio-local --build-arg TEMPORAL_FROM=temporal-local)
else
  ARGS=()
fi

for target in studio runtime temporal; do
  docker build -f deploy/laptop/Dockerfile --target "$target" "${ARGS[@]}" -t "th-laptop-$target:$TAG" .
done
docker image ls --format '{{.Repository}}:{{.Tag}} {{.Size}}' | grep '^th-laptop-'
