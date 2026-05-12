# Releasing ClaudeMigrator

The `Release` workflow (`.github/workflows/release.yml`) packs the app into an
MSIX, signs it, attaches the artifacts to a GitHub Release, and submits a
manifest to [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs).
The MSIX is built with `packaging/msix/Build-Msix.ps1`, which is also runnable
locally.

## Trigger

The workflow runs on either:

- Pushing a tag matching `v*` (recommended): `git tag v1.2.3 && git push origin v1.2.3`
- Manual dispatch from the Actions tab, supplying the version (e.g. `1.2.3`)

Both paths normalize to the same `vX.Y.Z` release tag and `X.Y.Z` package version.

## Required repository secrets

| Secret | Purpose | Required |
|---|---|---|
| `MSIX_CERT_BASE64` | Base64-encoded `.pfx` used to sign the MSIX | optional |
| `MSIX_CERT_PASSWORD` | Password for the `.pfx` | optional (if PFX is unencrypted) |
| `WINGET_PAT` | Classic GitHub PAT with `public_repo` scope. Used by `winget-releaser` to fork `microsoft/winget-pkgs` and open the manifest PR. | required for `winget` job |

If `MSIX_CERT_BASE64` is missing, `Build-Msix.ps1` falls back to a fresh
self-signed `CN=ClaudeMigrator` cert created on the runner. That cert is
attached to the release as `ClaudeMigrator.cer`, but its thumbprint will change
every build, which breaks WinGet's signature pinning. Supply a stable PFX for
production releases.

### Generating a self-signed PFX for the secret

```powershell
$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject "CN=ClaudeMigrator" `
    -KeyUsage DigitalSignature `
    -KeyExportPolicy Exportable `
    -CertStoreLocation Cert:\CurrentUser\My `
    -NotAfter (Get-Date).AddYears(5)

$password = ConvertTo-SecureString -String 'pick-a-password' -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath ClaudeMigrator.pfx -Password $password | Out-Null

# Encode for the GitHub secret:
[Convert]::ToBase64String([IO.File]::ReadAllBytes('ClaudeMigrator.pfx')) | Set-Clipboard
```

Paste into the `MSIX_CERT_BASE64` secret. Store the password in
`MSIX_CERT_PASSWORD`.

### Generating the WinGet PAT

Create a classic personal access token on GitHub with scope `public_repo` only.
The token only needs to fork and write to a fork of `microsoft/winget-pkgs`.
Store it in `WINGET_PAT`.

## Job graph

1. `build`:
    - Restore, run xUnit suite.
    - Run `Build-Msix.ps1` with the resolved version, the PFX from secrets,
      `-SkipInstall -SkipCertTrust` (CI never installs the cert into the
      runner's machine store).
    - Stage `ClaudeMigrator.msix` and `ClaudeMigrator.cer` into
      `release-artifacts/`.
    - Upload the same files as an Actions artifact (30-day retention) and
      attach them to the GitHub Release for the tag.
2. `winget` (needs `build`):
    - `vedantmgoyal2009/winget-releaser@v2` builds and submits the manifest to
      `microsoft/winget-pkgs` for `SharpNinja.ClaudeMigrator`. Skipped for
      `0.x` versions to avoid noisy first-release PRs while the package is
      still wired up.

## Local dry-run

Build the MSIX locally with the same version logic CI uses:

```powershell
pwsh -File packaging/msix/Build-Msix.ps1 -Version 1.2.3 -SkipInstall -SkipCertTrust
```

Outputs land in `packaging/msix/out/`. The script also prints
`InstallerSha256` and `SignatureSha256`; both are written to
`$env:GITHUB_OUTPUT` when CI is running so downstream steps can pin them.

## Bumping the version

`Build-Msix.ps1 -Version 1.2.3` writes `Version="1.2.3.0"` into the staged
`AppxManifest.xml`. There is no hand-edited version in any other file. To
release a new version, tag the commit:

```powershell
git tag v1.2.3
git push origin v1.2.3
```

The workflow does the rest.
