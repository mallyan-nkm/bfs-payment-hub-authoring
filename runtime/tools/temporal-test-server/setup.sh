#!/bin/bash
# Resolve Temporal's test server from Maven Central (Java 17+ and Maven needed) and write its classpath.
set -e
cd "$(dirname "$0")"
mvn -q -B dependency:build-classpath -Dmdep.outputFile=classpath.txt
echo "classpath.txt written"
