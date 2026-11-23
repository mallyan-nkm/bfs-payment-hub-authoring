#!/bin/sh
# A pack's native module, built ahead of time from its code-behind project (spec R-09-120, R-09-127), for a studio with no .NET SDK:
#
#   runtime/tools/native-module/build.sh <scope> <project dir> <out root>
#   runtime/tools/native-module/build.sh uae.wps runtime/src/TransactHub.Activities.Wps /opt/th/native-modules
#   runtime/tools/native-module/build.sh p runtime/src/TransactHub.Activities.Platform.Native /opt/th/native-modules
#   runtime/tools/native-module/build.sh --signers      the trusted-signers document of the signing key, for TH_NATIVE_MODULE_SIGNERS
#
# writes <out root>/<scope>/<assembly>.dll and native-module.json (its scope, assembly, native contract, source hash and the SHA-256
# of each assembly), the folder the studio takes with TH_NATIVE_MODULES=<out root> and the native host with
# TH_NATIVE_DEV_MODULES=<out root>. The build is the one the studio makes with TH_NATIVE_MODULES=build: Release, deterministic, the
# module's own assembly only (the contract and the image's runtime software are never carried).
#
# Signed (R-09-127) as the studio signs a distribution: release.json, a th-release-statement/1 of kind native-module for
# native-module/<scope>, naming every other file of the folder with its SHA-256 and the module's assemblies, contract and source
# hash; release.sig, an ECDSA P-256 signature with SHA-256 over it, DER, Base64. The key is read only from the environment:
# TH_NATIVE_SIGNING_KEY=env:<NAME>, NAME holding the PEM private key. It is never written to a file. Without it the folder is
# written unsigned, and a studio refuses it unless TH_NATIVE_MODULES_UNVERIFIED is dev or test (docs/runtime/PACKAGING-AND-DEPLOYMENT.md
# section 3.1). The source hash is the studio's (studio/src/export/native-modules.ts sourceSha256): the SHA-256 of what sha256sum
# lists for every .cs and .csproj file outside bin and obj, in the byte order of their paths.
set -eu
here="$(cd "$(dirname "$0")/../../.." && pwd)"

key_pem() {
  ref="${TH_NATIVE_SIGNING_KEY:-}"
  [ -n "$ref" ] || return 1
  case "$ref" in
    env:*) name="${ref#env:}"
      case "$name" in ''|[0-9]*|*[!A-Za-z0-9_]*) echo "TH_NATIVE_SIGNING_KEY names no environment variable: $ref" >&2; exit 2 ;; esac ;;
    *) echo "TH_NATIVE_SIGNING_KEY must be env:<NAME>: the module signing key is read from the environment only" >&2; exit 2 ;;
  esac
  eval "pem=\${$name:-}"
  [ -n "$pem" ] || { echo "TH_NATIVE_SIGNING_KEY names $name, which is not set" >&2; exit 2; }
  printf '%s\n' "$pem"
}

key_id() { key_pem | openssl pkey -pubout -outform DER 2>/dev/null | sha256sum | cut -c1-32; }

if [ "${1:-}" = "--signers" ]; then
  key_pem >/dev/null || { echo "Set TH_NATIVE_SIGNING_KEY=env:<NAME>" >&2; exit 2; }
  pub="$(key_pem | openssl pkey -pubout 2>/dev/null | awk '{printf "%s\\n", $0}')"
  printf '{"kind":"trusted-signers","keys":[{"keyId":"%s","publicKey":"%s"}],"revoked":[]}\n' "$(key_id)" "$pub"
  exit 0
fi

scope="$1"; project="$2"; out="$3/$1"
csproj="$(ls "$project"/*.csproj | head -n 1)"
assembly="$(sed -n 's:.*<AssemblyName>\(.*\)</AssemblyName>.*:\1:p' "$csproj")"
[ -n "$assembly" ] || assembly="$(basename "$csproj" .csproj)"
contract="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$here/runtime/src/TransactHub.Activities.Contract/TransactHub.Activities.Contract.csproj")"
source="sha256:$(cd "$project" && find . \( -name bin -o -name obj \) -prune -o -type f \( -name '*.cs' -o -name '*.csproj' \) -print \
  | sed 's:^\./::' | LC_ALL=C sort | while IFS= read -r f; do sha256sum "$f"; done | sha256sum | cut -d' ' -f1)"
rel="$(cd "$project" && pwd)"; rel="${rel#"$here"/}"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
MSBUILDDISABLENODEREUSE=1 dotnet build "$csproj" -c Release --nologo -v q "-p:OutDir=$tmp/" -p:Deterministic=true -p:ContinuousIntegrationBuild=true -nodeReuse:false -p:UseSharedCompilation=false
rm -rf "$out" && mkdir -p "$out"
cp "$tmp/$assembly.dll" "$out/"
hex="$(sha256sum "$out/$assembly.dll" | cut -d' ' -f1)"
cat > "$out/native-module.json" <<EOF
{
  "format": "th-native-module/1",
  "scope": "$scope",
  "assembly": "$assembly",
  "contract": "$contract",
  "project": "$rel",
  "sourceSha256": "$source",
  "files": [{ "name": "$assembly.dll", "sha256": "sha256:$hex" }]
}
EOF

if key_pem >/dev/null 2>&1; then
  files="$(cd "$out" && for f in $(ls | LC_ALL=C sort); do printf '{"path":"%s","sha256":"%s"},' "$f" "$(sha256sum "$f" | cut -d' ' -f1)"; done)"
  # Canonical JSON (RFC 8785): keys in order, no white space; every value here is plain ASCII.
  printf '{"assemblies":[{"name":"%s.dll","sha256":"sha256:%s"}],"assembly":"%s","changeSet":null,"contract":"%s","files":[%s],"format":"th-release-statement/1","keyId":"%s","kind":"native-module","release":"%s","releaseId":"native-module/%s","scope":"%s","signedAt":"%s","sourceSha256":"%s","target":"native","tenant":null,"workflow":""}' \
    "$assembly" "$hex" "$assembly" "$contract" "${files%,}" "$(key_id)" "$contract" "$scope" "$scope" "$(date -u +%Y-%m-%dT%H:%M:%S.000Z)" "$source" > "$out/release.json"
  key_pem | openssl dgst -sha256 -sign /dev/stdin "$out/release.json" | base64 -w0 > "$out/release.sig"
  echo >> "$out/release.sig"
  signed="signed by key $(key_id)"
else
  signed="NOT signed (TH_NATIVE_SIGNING_KEY is not set): a studio takes it only with TH_NATIVE_MODULES_UNVERIFIED=dev or test"
fi
echo "native module $scope: $out/$assembly.dll (sha256:$hex, contract $contract, sources $source), $signed"
