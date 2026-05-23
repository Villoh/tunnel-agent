# Security Policy

## Reporting a Vulnerability

**Do not open a public issue for security vulnerabilities.**

Use [GitHub private vulnerability reporting](https://github.com/Villoh/tunnel-agent/security/advisories/new) to submit a report. Include:

- A description of the vulnerability
- Steps to reproduce
- Potential impact
- Any suggested mitigations (optional)

You will receive an acknowledgement within 72 hours. Once the issue is confirmed and a fix is ready, a coordinated disclosure will be made.

## Scope

- Credential handling and file-based session token storage
- Local HTTP proxy endpoint exposure
- Provider API key transmission
- Engine binary download and integrity verification

Out of scope: issues in third-party dependencies (report those upstream).
