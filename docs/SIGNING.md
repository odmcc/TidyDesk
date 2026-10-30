# Code signing

`TidyDesk.exe` is not signed yet, so Windows SmartScreen warns on first launch. Releases are built on GitHub Actions
(`.github/workflows/release.yml`), which is what free signing for open-source projects requires.

## One-time setup (free, via SignPath Foundation)

1. Apply at <https://signpath.org/foundation> for the `odmcc/TidyDesk` repository. Approval takes days to weeks.
2. In SignPath, create a project with slug `TidyDesk` and a signing policy `release-signing`.
   Its artifact configuration signs `TidyDesk.exe` inside the uploaded zip.
3. In GitHub, repository **Settings → Secrets and variables → Actions**:
   - Variable `SIGNPATH_ORGANIZATION_ID`: your SignPath organization ID
   - Secret `SIGNPATH_API_TOKEN`: a SignPath API token for a CI user
4. Push a tag (`git tag v3.1.0; git push origin v3.1.0`). The workflow signs the exe and attaches it to the release.

Until step 3 is done the workflow still works and releases an unsigned exe.

## Good to know

- A valid signature removes the "unknown publisher" label, but SmartScreen also judges **reputation**. A new certificate
  can still show a warning until enough people have downloaded the file.
- A self-signed certificate does not help: every user would have to install it by hand.
- Paid alternatives: Azure Artifact Signing (about $10/month, needs identity validation) or a commercial certificate.
