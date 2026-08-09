# Contributing to Easyaller

Thanks for helping improve Easyaller. It is pre-alpha software for safe, profile-based Windows workstation provisioning. Small, focused changes with tests and clear boundaries are easier to review and safer to maintain.

## Before opening a change

1. Read [PRODUCT_SPEC.md](PRODUCT_SPEC.md) and [TASKS.md](TASKS.md). Do not build undocumented OOBE workarounds, automatic internal-disk selection, ISO downloading, or destructive USB preparation.
2. Keep profiles neutral. Do not commit organization names, domains, proxies, installer binaries, production screenshots, ISO files, exported deployment packages, passwords, tokens, or credentials.
3. Add a focused test for behavior changes. Documentation-only changes should still be checked for broken links and inaccurate capability claims.
4. Run the following commands locally:

   ```sh
   dotnet build Easyaller.slnx
   dotnet test Easyaller.slnx --no-build
   ```

5. Keep one concern per pull request. Explain the user-visible behavior, safety impact, verification performed, and any remaining limitation.

## Profiles and examples

Reusable `*.wpprofile.json` files are ignored by default because real profiles can contain confidential, organization-specific non-secret data. The only allowed committed profiles are neutral examples in [examples/profiles](examples/profiles) and test fixtures. Start from [neutral-workstation.wpprofile.json](examples/profiles/neutral-workstation.wpprofile.json), replace its `profileId`, and review every field before importing it.

## Interface language

The current desktop interface is Russian by product decision. Contributor-facing documents, identifiers, source code, tests, and GitHub metadata use English unless a document is an explicit Russian translation.

## Reporting security issues

Do not open public issues for suspected vulnerabilities, leaked credentials, unsafe media-write behavior, or a way to bypass the safety model. Follow [SECURITY.md](SECURITY.md) instead.
