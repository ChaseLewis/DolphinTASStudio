set windows-shell := ["powershell.exe", "-NoLogo", "-NoProfile", "-Command"]

# Reuse the persistent local workspace and its running instance; rebuild once closed.
dev:
    powershell.exe -NoLogo -NoProfile -File scripts/dev.ps1

# Start a separate local workspace with isolated settings, recovery and loose states.
dev-unique:
    powershell.exe -NoLogo -NoProfile -File scripts/dev.ps1 -Unique

# Build native and managed code, run tests, and publish a self-contained release.
build:
    powershell.exe -NoLogo -NoProfile -File scripts/build.ps1 -Publish -PublishDirectory artifacts/prod
