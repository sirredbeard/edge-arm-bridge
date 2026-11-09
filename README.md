# edge-arm-bridge

Mirror a Microsoft Edge profile from the x86-64 Edge running under FEX to a native arm64 Chromium, so an agent can use the signed-in profile at native speed.

Edge under FEX works. It is also slow. The arm64 Chromium on the same board is fast, but it has no Microsoft sign-in and no way to pull a profile down from Microsoft sync. This project fills that gap: sign into Edge once, by hand, and a small .NET 11 service copies the profile across to Chromium once a day.

## Contents

- Verdict - the short answer
- How it works - the three parts
- Run it - setup, manual sync, and the daily timer
- Repository layout - what lives where
- License - Apache 2.0

## Verdict

It is doable, with one hard condition.

The profile files copy cleanly. Passwords, cookies, and sign-in tokens do not travel with the files. They are encrypted with a secret that lives in GNOME Keyring, under an entry named for Edge, and Chromium looks for the same kind of entry under it's own name. A copy made without moving that secret looks fine on disk and fails in the browser: in the Podman test in findings/, Chromium opened a copied profile, kept all 664 login rows, dropped the cookie count from 151 to 0, and logged a token decrypt failure.

So the sync has three jobs, not one: (1) copy the files, (2) move the Safe Storage secret from the Edge keyring entry to the Chromium keyring entry, (3) prove the copy can decrypt before it replaces the live Chromium profile. The full test record is in findings/2026-11-08-profile-mirroring-investigation.md.

## How it works

The bundle carries everything. One tar holds FEX and the amd64 Edge build as extracted binaries, a trimmed x86-64 sysroot built from Edge's library closure (750 MB where the full FEX rootfs is 2.9 GB, and 680 MB of that is Edge itself), an arm64 Chromium build of the same major version, and the sync service. No package manager runs on the user's machine, on Ubuntu or Fedora or anything else: a build script downloads and extracts the .deb files on a build machine, and install.sh on the user's machine only writes per-user files. Findings 10 to 14 in findings/ have the test record, including the cold untar test.

The first run of Edge is the one manual step. A pre-written preferences file skips the first-run screens, the default browser prompt, and the tour. What it cannot skip is the Microsoft sign-in itself, and it should not: sign-in, sync, and any multi-factor prompt happen in the headed Edge window, once.

After that the service does the daily work. It waits until both browsers are closed, copies the Edge profile into a staging directory, moves the keyring secret, runs a decrypt check against a sample of the copied passwords and cookies, and only then swaps the staging copy in as the Chromium profile. The old Chromium profile stays as a dated backup. If any step fails, the live profile is left alone and the service writes status.json with the reason and exits with a code that says which step failed.

The mirror is one-way. Edge is the source of truth. Changes made in Chromium do not flow back, and Edge-only stores (Collections, the Edge wallet, Edge sessions) have no Chromium equivalent and do not transfer.

## Run it

Install. This downloads the newest release bundle (the compiled sync service, a self-contained FEX that needs no host install, and the Edge image as one squashfs file), untars it into ~/.local/opt/edge-arm-bridge, and points FEX at it's own bundled loader:

```bash
$ curl -fsSL https://github.com/sirredbeard/edge-arm-bridge/releases/latest/download/edge-arm-bridge.sh | bash -s -- --install
```

Configure. One manual step, in a desktop session: Edge opens under FEX, sign into the Microsoft account, let sync finish, close the window:

```bash
$ ~/.local/opt/edge-arm-bridge/edge-arm-bridge.sh --configure
```

Service. A systemd user timer wakes Edge every 6 hours, gives it 5 minutes to sync with Microsoft, then mirrors the profile to Chromium:

```bash
$ ~/.local/opt/edge-arm-bridge/edge-arm-bridge.sh --service
```

By hand, when wanted: --sync mirrors once, --wake runs one wake-and-mirror. A dry run of the mirror, which copies to staging and runs the checks but never swaps, is the service in the bundle called with --once --dry-run.

To build a fresh bundle with the newest FEX, Edge stable, and Chromium:

```bash
$ ./packaging/build-bundle.sh
```

The build runs on an arm64 Ubuntu build machine and is the only place a package manager is touched, and there only to download .deb files into a scratch directory. .github/workflows/build-bundle.yml wraps the same script for scheduled rebuilds. It is a scaffold: the script is tested, the workflow has not run in CI yet.

## Repository layout

```text
edge-arm-bridge/
|-- findings/     # investigation notes and test records
|-- packaging/    # bundle build script, sysroot walker, and the bundle's own scripts
|-- setup/        # earlier host setup script, superseded by packaging/
|-- src/          # the .NET 11 sync service
|-- systemd/      # user service and daily timer
|-- tools/        # profile probe used by the decrypt check
|-- LICENSE
`-- README.md
```

Apache 2.0. See LICENSE.
