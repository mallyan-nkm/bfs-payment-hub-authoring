#!/bin/sh
# A scheme's native module, built ahead of time from its code-behind project (spec R-09-120), for a studio with no .NET SDK:
#
#   runtime/tools/native-module/build.sh <scope> <project dir> <out root>
#   runtime/tools/native-module/build.sh uae.wps runtime/src/TransactHub.Activities.Wps /opt/th/native-modules
#
# writes <out root>/<scope>/<assembly>.dll and native-module.json (its scope, assembly, native contract and SHA-256), the folder
# the studio takes with TH_NATIVE_MODULES=<out root> and the native host with TH_NATIVE_DEV_MODULES=<out root>. The build is the
# one the studio makes with TH_NATIVE_MODULES=build: Release, deterministic, the module's own assembly only (the contract and the
# platform stay in the runtime image).
set -eu
scope="$1"; project="$2"; out="$3/$1"
here="$(cd "$(dirname "$0")/../../.." && pwd)"
csproj="$(ls "$project"/*.csproj | head -n 1)"
assembly="$(sed -n 's:.*<AssemblyName>\(.*\)</AssemblyName>.*:\1:p' "$csproj")"
[ -n "$assembly" ] || assembly="$(basename "$csproj" .csproj)"
contract="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$here/runtime/src/TransactHub.Activities.Contract/TransactHub.Activities.Contract.csproj")"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
MSBUILDDISABLENODEREUSE=1 dotnet build "$csproj" -c Release --nologo -v q "-p:OutDir=$tmp/" -p:Deterministic=true -p:ContinuousIntegrationBuild=true -nodeReuse:false -p:UseSharedCompilation=false
mkdir -p "$out"
cp "$tmp/$assembly.dll" "$out/"
sha="sha256:$(sha256sum "$out/$assembly.dll" | cut -d' ' -f1)"
cat > "$out/native-module.json" <<EOF
{
  "format": "th-native-module/1",
  "scope": "$scope",
  "assembly": "$assembly",
  "contract": "$contract",
  "files": [{ "name": "$assembly.dll", "sha256": "$sha" }]
}
EOF
echo "native module $scope: $out/$assembly.dll ($sha, contract $contract)"
