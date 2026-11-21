#!/bin/bash
# Temporal's time-skipping test server on the port given ($1). The .NET SDK starts it through this
# script (TestServerOptions.ExistingPath), as it would start the native build it cannot download here.
cd "$(dirname "$0")"
exec java -cp "$(cat classpath.txt)" io.temporal.testserver.TestServer "$@"
