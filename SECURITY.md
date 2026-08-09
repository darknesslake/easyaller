# Security policy

## Supported versions

Easyaller is pre-alpha. Security fixes are applied to the current `main` branch only. There are no supported release branches yet.

## Reporting a vulnerability

Do not report suspected vulnerabilities in a public issue. Prefer GitHub's private vulnerability-reporting flow when it is enabled for this repository. If it is unavailable, contact the repository owner through GitHub and ask for a private reporting channel before sending technical details.

Include a minimal reproduction, affected commit or build, Windows edition and build when relevant, expected and actual behavior, and whether the issue can write to a disk, expose a secret, or bypass a safety check. Do not send real credentials, ISO images, deployment packages, customer data, or destructive proof-of-concept code.

## Scope and response

Reports are most useful when they concern profile import or export, package integrity, answer-file handling, temporary credentials, Windows Setup execution, USB target selection, volume binding, or destructive-write confirmation. The maintainer will acknowledge a private report, assess impact, and coordinate a fix before public disclosure when feasible.

## Safe research boundary

Test only on data and removable media you own or are explicitly authorized to use. Never target a production workstation, internal disk, corporate account, or third-party system while researching an issue.
