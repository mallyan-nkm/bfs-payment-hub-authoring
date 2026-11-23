#!/bin/sh
# Keeps one copy of each file the runtime's hosts share (deploy/laptop/build.sh and the Dockerfile's build stage).
# Every host is published into its own folder, so the same NuGet assembly appears once per host. Each file of 64 KiB or more
# that appears in two folders or more is kept once under <root>/.shared/<hash>/<name>, and every folder gets a relative
# symbolic link to it: .NET loads an assembly through a link, and Docker copies links as links, so an image holds it once.
set -eu
root=$(realpath "$1")
shared="$root/.shared"
list=$(mktemp)
# A host's own assembly stays where it is: dotnet runs <host>.dll from its real folder, beside its runtimeconfig.json.
find "$root" -path "$shared" -prune -o -type f -size +64k -print | while read -r f; do
  case "$f" in *.dll) [ -e "${f%.dll}.runtimeconfig.json" ] && continue ;; esac
  printf '%s %s\n' "$(sha256sum "$f" | cut -c1-32)" "$f"
done | sort > "$list"
n=0
for h in $(cut -d' ' -f1 "$list" | uniq -d); do
  keep=""
  for f in $(grep "^$h " "$list" | cut -d' ' -f2-); do
    if [ -z "$keep" ]; then
      keep="$shared/$h/$(basename "$f")"
      mkdir -p "$(dirname "$keep")"
      mv "$f" "$keep"
      n=$((n + 1))
    else
      rm "$f"
    fi
    ln -s "$(realpath --relative-to="$(dirname "$f")" "$keep")" "$f"
  done
done
rm -f "$list"
echo "dedupe: $n shared file(s); $(du -sh "$root" | cut -f1) in all"
