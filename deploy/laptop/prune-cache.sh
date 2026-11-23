#!/bin/sh
# Removes the build cache of the laptop images (deploy/laptop/build.sh), entry by entry, by id. Only entries whose description
# is one of this Dockerfile's stages are removed; nothing else in the machine's build cache is touched.
#   prune-cache.sh          the intermediate stages: the build context and the copies of deploy/laptop/.build
#   prune-cache.sh --all    every stage of this Dockerfile (after the th-laptop-* images are removed)
set -eu
stages='local source for context|\[(dotnet|studio|temporal)-local '
[ "${1:-}" = "--all" ] && stages='local source for context|local source for dockerfile|\[(dotnet|studio|temporal)-local |\[(base|node|studio-bundle|studio|runtime|temporal) [0-9]+/[0-9]+\]|mkdir -p /opt/th /var/lib/transacthub'
for pass in 1 2 3; do
  docker buildx du --verbose 2>/dev/null | awk -v re="$stages" '
    /^ID:/ { id = $2 }
    /^Description:/ { d = $0 }
    /^$/ { if (id != "" && d ~ re) print id; id = ""; d = "" }
  ' | while read -r id; do docker builder prune -f --filter "id=$id" >/dev/null; done
done
docker buildx du 2>/dev/null | tail -1
