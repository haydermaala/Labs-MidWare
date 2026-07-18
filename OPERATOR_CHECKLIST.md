# Project Owner Handoff and Setup Checklist

## Before giving the prompt to Claude Code

- Create a new empty local project folder; do not point Claude at a directory containing unrelated personal files.
- Decide the temporary project name and repository owner/organization.
- Confirm whether Claude may only scaffold locally or may also create a private GitHub repository. Local-only is the recommended first session.
- Install/verify Git, Rust toolchain, Node LTS, pnpm, .NET SDK, Tauri prerequisites, and platform build tools.
- On Windows, prepare a Windows 11 test machine/VM and a known USB-to-RS-232 adapter.
- On macOS, prepare a recent macOS test machine and compatible adapter/driver.
- Obtain one analyzer’s exact model, firmware/software version, host-interface manual, license status, connection settings, test codes, and controlled access.
- Keep vendor manuals outside the repository unless redistribution is explicitly permitted.
- Prepare only synthetic sample identifiers and results for development.

## How to start Claude Code

1. Open a terminal in the new project folder.
2. Start Claude Code using your normal installed command.
3. Paste the entire contents of `CLAUDE_CODE_MASTER_PROMPT.md` as the first message.
4. Tell Claude the current authorization, for example: “You may inspect and scaffold locally. Do not create or modify GitHub, Railway, Cloudflare, DNS, billing, or production resources.”
5. Ask Claude to complete only Phase 0 and Phase 1 planning/scaffolding first.
6. Review its proposed ADRs, repository tree, dependency versions, security model, and first milestone before approving implementation.

## Browser-authenticated accounts

Being signed into GitHub, Railway, Cloudflare, or another service in Chrome does not authorize Claude to change it. Browser sessions can also expose broad personal privileges. Prefer official CLIs or narrowly scoped tokens, separate development projects, least-privilege roles, and explicit confirmation for every external mutation.

Claude must stop and ask before:

- Creating a repository, project, service, database, bucket, DNS record, domain, token, secret, deployment, or billing resource.
- Installing a GitHub app or granting OAuth/account permissions.
- Pushing code, opening/merging a PR, changing branch protection, or publishing a release.
- Uploading any file to an external service.
- Using real analyzer/patient data or sending bytes to a physical analyzer.
- Creating production infrastructure or changing production configuration.

## Safe first-session deliverables

- `docs/product/PRD.md`
- `docs/architecture/system-context.md`
- `docs/security/threat-model.md`
- `docs/validation/validation-strategy.md`
- Initial ADRs and risk register
- Proposed repository tree and dependency/version matrix
- Phase 1 backlog with acceptance criteria
- Local repository scaffold only after review

## Information Claude should ask you for, not guess

- Product/repository name and ownership.
- Deployment countries and data-residency constraints.
- Intended regulatory/quality framework and responsible advisor.
- First analyzer exact identity and licensed interface documentation.
- Target LIS/HIS protocol/profile and sample contracts.
- Retention requirements and whether cloud may ever hold clinical payloads.
- Signing identities for Windows/macOS (later).
- Production availability and recovery objectives (later).

## Approval phrase template

Use a narrow statement such as:

> I approve creating a private development repository named `<name>` under `<owner>` and pushing the reviewed local scaffold to a new branch. Do not create Railway, Cloudflare, DNS, billing, staging, or production resources.

Never give a broad instruction such as “use all my accounts and do whatever is needed.”
