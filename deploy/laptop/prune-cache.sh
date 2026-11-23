#!/bin/sh
# Removes the build cache of the laptop images' intermediate stages (deploy/laptop/build.sh): the build context and the
# copies of deploy/laptop/.build that only feed the final images. Only entries whose description is one of those stages are
# removed, one by one, by id; nothing else in the machine's build cache is touched.
set -eu
docker buildx du --verbose 2>/dev/null | awk '
  /^ID:/ { id = $2 }
  /^Description:/ { d = $0 }
  /^$/ { if (id != "" && (d ~ /local source for context/ || d ~ /\[(dotnet|studio|temporal)-local /)) print id; id = ""; d = "" }
' | while read -r id; do docker builder prune -f --filter "id=$id" >/dev/null; done
docker buildx du 2>/dev/null | tail -1
