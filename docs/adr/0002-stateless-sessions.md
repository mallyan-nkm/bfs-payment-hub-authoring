# ADR 0002. Sessions in sealed cookies, so the studio runs as several instances

Status: accepted, 22 November 2026 (Issue I-72, R-11-45). The command line's sign-in, which uses the same seal, is R-11-46 (Issue I-71).

## Context

The studio held its sessions and its sign-ins in progress in a map in its own process. A second instance behind a load balancer did not know them, so a balancer had to keep each browser on one instance, and a restart signed everyone out. The studio's only store is the GitHub content branch (R-11-21). A session is not content: writing one to the branch on every sign-in would put personal sign-in data into Git history and a commit on every request. Adding a database or a cache only for sessions would add a store the studio does not otherwise have.

## Decision

1. **The session is its cookie.** `th_session` carries its own claims: a random session id, the identity, the user it maps to, the CSRF token, the absolute expiry, when it was last used, and the provider's groups when the studio maps them. It is sealed with AES-256-GCM, so it can be neither read nor changed by the browser.
2. **A sign-in in progress is its cookie too.** `th_login` carries the state, the PKCE verifier, the nonce, where to return and a ten-minute expiry, sealed the same way. The callback may land on any instance. The provider's authorization code is single use, so a replayed callback fails at the provider.
3. **One key per purpose.** The keys are derived from the session key by HKDF-SHA256, one each for a session, a sign-in and a command line token, so one is never taken for another.
4. **The same key everywhere.** Every instance reads the key by reference (`TH_SESSION_KEY_REF=env:<NAME>`, the installation's secret store). In production the studio refuses to start without it. Elsewhere a random key per process is used and the studio says so at start.
5. **Idle time slides.** The session's last use is in the cookie. A cookie used more than a minute after it was sealed is sealed again with the new time and sent back. A session unused for `TH_SESSION_IDLE_MINUTES` ends on every instance, as does one past its absolute expiry.
6. **Sign-out.** The cookie is cleared, and the instance that signed it out refuses that session id until it would have expired. That list is the only thing an instance holds, and it holds only sign-outs.
7. **The ID token is not kept.** It would not fit a cookie with room to spare. Sign-out at the provider names the studio by `client_id` instead of `id_token_hint`.
8. **A heartbeat with the provider (owner's decision of 23 November 2026, Issue I-139, R-11-50).** The provider's refresh token is sealed in the session. Every `TH_SESSION_HEARTBEAT_MINUTES` (5) a session in use is renewed with it on whichever instance it reaches, and sealed again with the new token and the time of the check. A session the provider refuses ends there, and on every other instance at its own heartbeat. The page asks for the heartbeat without counting it as use. The defaults are 8 hours, 30 minutes idle and 1 hour for the command line.

## Consequences

- Several instances, no sticky sessions, and a restart keeps everyone signed in. Nothing new is stored, and nothing is written to disk (R-11-24).
- A copy of a signed-out cookie is still accepted by another instance until its next heartbeat, at most `TH_SESSION_HEARTBEAT_MINUTES`, when the provider's session ended with it; a user disabled at the provider keeps a session at most that long. Ending a session on every instance at the same moment would need shared state; the owner chose the heartbeat instead (Issue I-139). A refresh token rotated on two instances at once: I-148.
- Rotating the session key signs everyone out. The rate limits (R-11-41) count per instance, so with `n` instances a caller can make `n` times the limit.
- A cookie grows with the provider's groups. A provider that sends many groups should send only the studio's, or the studio should not map groups (`TH_OIDC_GROUPS_CLAIM` unset).
