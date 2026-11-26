# Security policy

## Supported versions

Yanki is pre-release. Only the M0 skeleton exists (see `README.md`), no version has been
released and no version is supported. Do not run it in production or expose a node to an
untrusted network.

## Reporting a vulnerability

Please report vulnerabilities privately through GitHub's private vulnerability reporting on
this repository: open the **Security** tab and choose **Report a vulnerability**. Do not open a
public issue, pull request or discussion for a suspected vulnerability.

Include what you found, the commit you tested, and the steps to reproduce it. Reports are read
by the maintainer; there is no bug bounty.

## Known pre-release limitations

These are known and documented. Reports that only restate them are not treated as new
vulnerabilities, but reports showing that one of them is worse than described here are welcome.

1. **Development tokens** (`Yanki:Realtime:AllowDevTokens` accepts any `dev:<name>` token
   without verification). A default install is not exposed: the switch is `false` in
   `appsettings.json` and in the installer template, the Development settings file that turns
   it on is excluded from publish output and from the MSI, and the host refuses to start with
   it outside the Development environment unless the node is an explicit `AllowInsecureHttp`
   lab node (item 8).
2. **No real authentication before milestone M1.** A default install is not exposed: the
   handshake fails closed and every `Hello` is refused, so no session can be opened at all.
3. **No rate limiting and no per-IP or pre-authentication connection cap on `/ws`** (planned
   for M2). A default install is exposed: anyone who can reach a node can hold unauthenticated
   WebSocket connections open for up to the 5-second `Hello` timeout each, which is a
   resource-exhaustion surface. Only `MaxConnectionsPerUser` exists, and it applies after
   authentication.
4. **`GET /api/v1/ping` is anonymous** and returns the version, node id, roles and server
   time; the scripted installer sets the node id to the computer name by default. A default
   install is exposed to this low-value information disclosure. `/readyz` is anonymous too,
   but returns error details only in the Development environment.
5. **A TLS certificate outside its validity period is accepted at startup with a warning
   only.** This concerns an operator's certificate, not the default install; clients reject
   such a certificate, and `yanki doctor` reports it.
6. **Cookie-authenticated WebSocket connections will need an `Origin` allow-list**
   (`docs/protocol.md`). A default install is not exposed: no cookie authentication exists
   today.
7. **Service account defaults.** The Yanki service (MSI and scripted installer) and the
   LiveKit service both default to `LocalSystem`; a gMSA or `NT AUTHORITY\NetworkService` is
   the documented hardened choice. LiveKit runs at High priority, and its service wrapper
   (WinSW 2.12.0) is unsigned and therefore pinned by SHA-256. A default install runs with
   these defaults; they are hardening notes, not exploitable on their own.
8. **An http-only lab install** renders `AllowInsecureHttp=true`, which also lifts the
   development-certificate and development-token guards. A default install is not exposed:
   this happens only when the operator passes http-only URLs, and the installer warns that it
   is for lab use only.
9. **The scripted installer's default SQL connection string sets
   `TrustServerCertificate=true`**, so a scripted install that keeps the default does not
   validate the SQL Server certificate. A default scripted install is exposed to this on the
   path between the node and SQL Server; pass a connection string with
   `TrustServerCertificate=false` and a trusted certificate on the SQL Server.
