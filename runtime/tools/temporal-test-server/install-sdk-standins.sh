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
    # The SDK stops its dev server by killing it outright, which a container would outlive: a watcher
    # removes the container as soon as the process that started it is gone.
    cat > "$tmp/temporal-sdk-dotnet-$ver" <<'STANDIN'
#!/bin/bash
name="th-temporal-dev-$$"
owner=$PPID
( while kill -0 "$owner" 2>/dev/null && kill -0 $$ 2>/dev/null; do sleep 2; done; docker rm -f "$name" >/dev/null 2>&1 ) &
exec docker run --rm -i --name "$name" --network host temporalio/temporal:1.5.1 "$@"
STANDIN
    chmod +x "$tmp/temporal-test-server-sdk-dotnet-$ver" "$tmp/temporal-sdk-dotnet-$ver"
    echo "stand-ins for SDK $ver in $tmp"
  done
done
