#!/bin/bash
# For machines that cannot reach temporal.download (as this build machine cannot): the Temporal .NET SDK
# reuses a server it finds in its download cache, named <artifact>-sdk-dotnet-<version> in the temp folder.
# This puts stand-ins there for the given SDK versions (default: the TransactHub source's 1.12.0.0 and the
# runtime's 1.22.0.0): the time-skipping test server from Maven Central (run.sh), and the CLI dev server
# from its Docker image. Run setup.sh first.
set -e
here="$(cd "$(dirname "$0")" && pwd)"
tmp="${TMPDIR:-/tmp}"
for v in "${@:-1.12.0.0 1.22.0.0}"; do
  for ver in $v; do
    printf '#!/bin/bash\nexec "%s/run.sh" "$@"\n' "$here" > "$tmp/temporal-test-server-sdk-dotnet-$ver"
    printf '#!/bin/bash\nexec docker run --rm -i --network host temporalio/temporal:1.5.1 "$@"\n' > "$tmp/temporal-sdk-dotnet-$ver"
    chmod +x "$tmp/temporal-test-server-sdk-dotnet-$ver" "$tmp/temporal-sdk-dotnet-$ver"
    echo "stand-ins for SDK $ver in $tmp"
  done
done
