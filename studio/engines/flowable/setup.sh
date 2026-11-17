#!/usr/bin/env bash
# Fetch Maven into .bin/ (gitignored) and build the Flowable harness. Needs Java 17 or later.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$here/.bin"
if [ ! -x "$here/.bin/apache-maven-3.9.9/bin/mvn" ]; then
  curl -sSfL https://repo1.maven.org/maven2/org/apache/maven/apache-maven/3.9.9/apache-maven-3.9.9-bin.tar.gz | tar xz -C "$here/.bin"
fi
cd "$here/harness"
"$here/.bin/apache-maven-3.9.9/bin/mvn" -q -B -Dmaven.repo.local="$here/.bin/m2" package
echo "Harness built: $here/harness/target"
