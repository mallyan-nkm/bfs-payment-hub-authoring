# The laptop stack's images (deploy/laptop/README.md, spec R-09-80): three targets from one file, each on one pinned base.
#
#   --target studio     th-laptop-studio     the studio's API and web app (Node 22), with the WPS native module built ahead of
#                                            time (TH_NATIVE_MODULES): the native assemblies it puts in each distribution
#   --target runtime    th-laptop-runtime    every runtime host: the release API, the operations and service consoles' APIs and
#                                            web app, the test bench, the native host, the engine host supervisor, the harnesses,
#                                            the migrations and the laptop's development identity provider (.NET 10). The native
#                                            host holds the platform's native activities only: a scheme's native code (WPS's)
#                                            is never in this image; it comes in each distribution (R-09-120 to R-09-122)
#   --target temporal   th-laptop-temporal   the Temporal server and schema tool with the SQL Server plugin (runtime/temporal-mssql)
#
# Every target runs as the unprivileged user app (uid 1654), holds no key, password or token (they come from the environment or
# a mounted file at run time), and has a health check. By default each is built from source in the build stages below; with
# --build-arg DOTNET_FROM=dotnet-local STUDIO_FROM=studio-local TEMPORAL_FROM=temporal-local the same final stages take what
# deploy/laptop/build.sh --local built on the host (deploy/laptop/.build), for a machine whose Docker builds have no network.
# The context is the repository's root; deploy/laptop/Dockerfile.dockerignore keeps builds, packages and secrets out of it.

ARG ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0@sha256:12b1135f5f8b3c0a214f2a680ff2033dff4f684f445d6c697d9524ceeb303586
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0@sha256:bef136a371963aaa46ec97882845ef6f54dec15728bb4b128b1c2a400ffbc9b4
ARG NODE_IMAGE=node:22-bookworm-slim@sha256:e9f565d32859f0cbfb52125eafe8be39390f24dac302dc3cdf566e8ba3ab21ee
ARG GO_IMAGE=golang:1.26-bookworm@sha256:dc40a13b61304647ed7e5d581d246b86413dfc29d63c6d018310feb93cd98fc0
ARG DOTNET_FROM=dotnet-source-all
ARG STUDIO_FROM=studio-source
ARG TEMPORAL_FROM=temporal-source

# ---- Node, for its binary only ---------------------------------------------------------------------------------------------
FROM ${NODE_IMAGE} AS node

# ---- The one base of every target: ASP.NET Core 10 on Ubuntu, Node 22's binary, the folders the volumes mount on ------------
FROM ${ASPNET_IMAGE} AS base
COPY --from=node /usr/local/bin/node /usr/local/bin/node
RUN mkdir -p /opt/th /var/lib/transacthub/keys /var/lib/transacthub/idp /var/lib/transacthub/jwks /var/lib/transacthub/work /var/lib/transacthub/files /var/lib/transacthub/tokens \
 && chown -R app:app /var/lib/transacthub
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 NODE_ENV=production TH_SECRET_FILE_ROOTS=/var/lib/transacthub:/run/secrets
WORKDIR /opt/th

# ---- Build stages, from source ----------------------------------------------------------------------------------------------
FROM ${NODE_IMAGE} AS studio-source
WORKDIR /src/studio
COPY studio/package.json studio/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY studio/ ./
COPY spec/ /src/spec/
RUN npm run web:build && npm prune --omit=dev && mkdir -p /out && mv node_modules /out/node_modules && mv web/dist /out/web-dist

FROM scratch AS studio-local
COPY deploy/laptop/.build/studio-deps/node_modules /out/node_modules
COPY studio/web/dist /out/web-dist

FROM ${SDK_IMAGE} AS dotnet-source
WORKDIR /src
COPY runtime/ runtime/
COPY studio/engines/ studio/engines/
COPY deploy/laptop/dedupe.sh /src/dedupe.sh
RUN set -e; for p in src/TransactHub.Api src/TransactHub.Service.Api src/TransactHub.Operations.Api src/TransactHub.Native src/TransactHub.FileApi \
      tools/TransactHub.TestBench tools/TransactHub.EngineHost tools/TransactHub.TemporalHarness tools/TransactHub.DtfxHarness \
      tools/TransactHub.ElsaHarness tools/TransactHub.FlowableHarness tools/TransactHub.DevIdentity tools/TransactHub.Migrate; do \
      rid=""; [ "$p" = tools/TransactHub.TemporalHarness ] && rid="-r linux-x64 --self-contained false"; \
      dotnet publish "runtime/$p" -c Release --nologo -v q $rid -o "/out/opt/th/runtime/$p/bin/Release/net10.0" -p:UseAppHost=false -p:DebugType=none; done; \
    sh /src/dedupe.sh /out/opt/th/runtime; \
    sh runtime/tools/native-module/build.sh uae.wps runtime/src/TransactHub.Activities.Wps /out/native-modules
FROM ${NODE_IMAGE} AS ops-console-source
WORKDIR /src/runtime/ops-console
COPY runtime/ops-console/package.json runtime/ops-console/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY runtime/ops-console/ ./
RUN npm run build && mkdir -p /out/opt/th/runtime/ops-console && mv dist /out/opt/th/runtime/ops-console/dist
FROM scratch AS dotnet-source-all
COPY --from=dotnet-source /out/ /out/
COPY --from=ops-console-source /out/ /out/

FROM scratch AS dotnet-local
COPY deploy/laptop/.build/opt/ /out/opt/
COPY deploy/laptop/.build/native-modules/ /out/native-modules/

FROM ${GO_IMAGE} AS temporal-source
WORKDIR /src
COPY runtime/temporal-mssql/ ./
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /out/ ./cmd/...

FROM scratch AS temporal-local
COPY deploy/laptop/.build/temporal/ /out/

# The chosen sources (ARG in FROM picks a stage by name).
FROM ${STUDIO_FROM} AS studio-bits
FROM ${DOTNET_FROM} AS dotnet-bits
FROM ${TEMPORAL_FROM} AS temporal-bits

# ---- The studio bundle: one layer, the same in the studio and the runtime images ---------------------------------------------
FROM base AS studio-bundle
COPY --chown=root:root spec/ /opt/th/spec/
COPY --chown=root:root content-configurations/ /opt/th/content-configurations/
COPY --chown=root:root studio/ /opt/th/studio/
COPY --from=studio-bits /out/node_modules /opt/th/studio/node_modules
COPY --from=studio-bits /out/web-dist /opt/th/studio/web/dist

# ---- th-laptop-studio --------------------------------------------------------------------------------------------------------
FROM studio-bundle AS studio
# The scheme native modules the studio puts in a distribution, built from their code-behind projects in the build stage: the
# studio image has no .NET SDK to build them at publish (docs/runtime/PACKAGING-AND-DEPLOYMENT.md section 3.1, Issue I-223).
COPY --from=dotnet-bits /out/native-modules/ /opt/th/native-modules/
ENV TH_NATIVE_MODULES=/opt/th/native-modules
USER app
WORKDIR /opt/th/studio
EXPOSE 4317
HEALTHCHECK --interval=10s --timeout=5s --start-period=60s --retries=12 \
  CMD ["node", "-e", "fetch('http://127.0.0.1:'+(process.env.PORT||4317)+'/health/live').then(r=>process.exit(r.ok?0:1),()=>process.exit(1))"]
# Without TH_CONTENT_REPO: the studio on its in-memory fake GitHub seeded from content-configurations/ (npm run studio:dev);
# with it, the real content branch, the token from the environment only (TH_GITHUB_TOKEN).
CMD ["node", "/opt/th/deploy/laptop/bin/studio.mjs"]
COPY --chown=root:root deploy/laptop/bin/ /opt/th/deploy/laptop/bin/

# ---- th-laptop-runtime -------------------------------------------------------------------------------------------------------
FROM studio-bundle AS runtime
COPY --chown=root:root docs/north-star/ /opt/th/docs/north-star/
COPY --chown=root:root runtime/TransactHub.Runtime.slnx runtime/global.json /opt/th/runtime/
COPY --chown=root:root runtime/testbench/ /opt/th/runtime/testbench/
COPY --chown=root:root runtime/observability/ /opt/th/runtime/observability/
COPY --from=dotnet-bits /out/opt/th/ /opt/th/
COPY --chown=root:root deploy/laptop/bin/ /opt/th/deploy/laptop/bin/
COPY --chown=root:root deploy/laptop/identity/ /opt/th/deploy/laptop/identity/
COPY --chown=root:root deploy/laptop/banks/ /opt/th/deploy/laptop/banks/
# The migrate step brings Temporal's schemas up to the server's version as the owner, once, at the stack's start (R-09-141): the
# schema tool and its files, on SQL Server (runtime/temporal-mssql) and on PostgreSQL (Temporal's own, deploy/laptop/temporal/schema).
COPY --from=temporal-bits /out/temporal-sql-tool /opt/th/temporal/bin/
COPY --chown=root:root runtime/temporal-mssql/schema/ /opt/th/temporal/schema/
COPY --chown=root:root deploy/laptop/temporal/schema/ /opt/th/temporal/schema/
ENV TH_TESTBENCH_REPO=/opt/th TH_TESTBENCH_PREBUILT=1 ASPNETCORE_URLS=http://0.0.0.0:8080
USER app
EXPOSE 8080
# Each service names its host (deploy/laptop/compose.yaml); its health is its own /health/ready or /health.
HEALTHCHECK --interval=10s --timeout=5s --start-period=60s --retries=12 \
  CMD ["node", "/opt/th/deploy/laptop/bin/health.mjs"]
ENTRYPOINT ["node", "/opt/th/deploy/laptop/bin/runtime.mjs"]

# ---- th-laptop-temporal ------------------------------------------------------------------------------------------------------
FROM base AS temporal
COPY --from=temporal-bits /out/temporal-server /out/temporal-sql-tool /opt/th/temporal/bin/
COPY --chown=root:root runtime/temporal-mssql/schema/ /opt/th/temporal/schema/
COPY --chown=root:root runtime/temporal-mssql/config/ /opt/th/temporal/config/
COPY --chown=root:root deploy/laptop/temporal/ /opt/th/temporal/laptop/
COPY --chown=root:root deploy/laptop/bin/health.mjs /opt/th/deploy/laptop/bin/health.mjs
USER app
WORKDIR /opt/th/temporal
EXPOSE 7233
ENV TH_HEALTH_TCP=127.0.0.1:7233
HEALTHCHECK --interval=10s --timeout=5s --start-period=60s --retries=18 CMD ["node", "/opt/th/deploy/laptop/bin/health.mjs"]
ENTRYPOINT ["/bin/sh", "/opt/th/temporal/laptop/start.sh"]
