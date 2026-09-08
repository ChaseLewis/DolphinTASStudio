# Release maintenance

Studio uses Velopack 1.2.0, pinned in both the application package reference and
the repository's `dotnet-tools.json`. GitHub Releases hosts the public update feeds;
clients do not need a GitHub token. Keep feed JSON and `.nupkg` assets attached to
their releases. Each feed uses its own `win-x64-stable`, `win-x64-beta`, or
`win-x64-alpha` channel. Alpha and beta versions end with `-alpha.N` and `-beta.N`.
Stable versions have no suffix. Increment versions; never replace existing assets.

To prepare a local package using an already built native core:

```powershell
./scripts/build.ps1 -SkipNative -Publish -PublishDirectory artifacts/update-app -Version 0.2.0-alpha.1
./scripts/package-release.ps1 -PublishDirectory artifacts/update-app -OutputDirectory artifacts/update-release -Version 0.2.0-alpha.1 -Channel alpha
```

Use a fresh output directory. Packaging verifies the complete runtime, matching
assembly/package versions, and absence of a developer `tasstudio-data.path` marker.
The GitHub workflow invokes the same script after the native build and managed
tests. The initial implementation uses full packages rather than deltas.

The installer and portable ZIP contain Velopack's launcher and update helper.
Application startup disables Velopack's unconditional pending-update installation.
Studio checks/downloads asynchronously at startup; users can also check manually.
The selected channel and verified pending asset are persisted outside `current`,
per installation. A pending download can be installed while offline. SHA-256 and
file size are checked after download and again before handing off installation.

Studio waits until its normal close path has saved work and disposed the emulator.
A shared file lease covers every Studio instance and each worker/coordinator's
entire lifetime. A separate gate serializes runtime startup and the transition to
the updater. Startup refuses a competing update; update hooks and launches after
successful replacement run through Velopack before acquiring a lease. A busy
installation retains the pending update for the next Studio session. Failed apply
handoffs are recorded as `last-error.txt` in that installation's directory under
`%LOCALAPPDATA%/TasStudio/Updates`. Velopack also writes its own updater logs.

For an end-to-end smoke test, make two increasing packages in separate directories,
extract the older portable ZIP under `artifacts`, and use Velopack's `SimpleFileSource`
with a `TestVelopackLocator` pointing at that extracted installation to check and
download from the newer release directory. Queue that verified asset in the test
installation's update preferences, launch/close Studio, and confirm the installed
manifest advanced. Repeat with a worker lease held: the manifest must stay unchanged
until the worker releases it. Keep test data outside `current`; check that it survives
replacement. No test releases need to be published to GitHub.

Installer signing can be added to the packaging command when a signing identity is
available. Current packages are unsigned. Pin SDK and CLI together when upgrading
Velopack and repeat the packaged smoke test. Compatibility promises and any required
migrations are a 1.0 concern; retain working compatibility where practical before
then and document known replay/state changes in release notes.
