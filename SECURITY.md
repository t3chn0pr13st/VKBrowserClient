# Security policy

## Supported version

Security fixes are applied to the latest release and the `main` branch.

## Reporting a vulnerability

Please do not publish session files, cookies, tokens, signed upload URLs, RTMP credentials or
account data in a GitHub issue. Send a minimal report to `dmitry@technopriest.ru`; include the
affected version and reproduction steps, but replace all live credentials and identifiers with
synthetic values.

The repository relies on undocumented VK web behavior. A provider-side contract change is a
compatibility issue unless it exposes credentials, crosses an authorization boundary or causes
sensitive data to be logged or returned.
