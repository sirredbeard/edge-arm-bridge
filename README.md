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

The profile files copy cleanly. Passwords, cookies, and sign-in tokens do not travel with the files. They are encrypted with a secret that lives in GNOME Keyring, under an entry named for Edge, and Chromium looks for the same kind of entry under it's own name. A copy made without moving that secret looks fine on disk and fails in the browser: the login rows all arrive, the cookies decrypt to nothing, and the browser logs a token decrypt failure.

So the sync has three jobs, not one: (1) copy the files, (2) move the Safe Storage secret from the Edge keyring entry to the Chromium keyring entry, (3) prove the copy can decrypt before it replaces the live Chromium profile. The full test record is in the [investigation findings](findings/2026-11-08-profile-mirroring-investigation.md).

## How it works

The bundle carries everything. One tar holds FEX and the amd64 Edge build as extracted binaries, a trimmed x86-64 sysroot built from Edge's library closure (750 MB where the full FEX rootfs is 2.9 GB, and 680 MB of that is Edge itself), an arm64 Chromium build of the same major version, and the sync service. No package manager runs on the user's machine, on Ubuntu or Fedora or anything else: a build script downloads and extracts the .deb files on a build machine, and install.sh on the user's machine only writes per-user files.

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

Two notes. A release published before it's build finishes has a short window with no bundle asset on it, and --install in that window says so: publish as a draft, let the build attach the bundle, then publish. And --install also accepts a local bundle through EDGE_ARM_BRIDGE_TARBALL, which is how the project's own tests run it.

Fresh bundles are built by GitHub Actions on every release, from the newest FEX and Edge stable of that day. The mirror target can be any Chromium-family browser on the system: set chromiumBinaryPath in config.json (the board this was built on uses /usr/bin/google-chrome, Google's arm64 build, since Ubuntu's chromium is snap-only there).

By hand, when wanted: --sync mirrors once, --wake runs one wake-and-mirror. A dry run of the mirror, which copies to staging and runs the checks but never swaps, is the service in the bundle called with --once --dry-run.

To build a fresh bundle with the newest FEX, Edge stable, and Chromium:

```bash
$ ./packaging/build-bundle.sh
```

The build runs on an arm64 Ubuntu build machine and is the only place a package manager is touched, and there only to download .deb files into a scratch directory. .github/workflows/build-bundle.yml wraps the same script and runs it on every release, attaching the fresh bundle to that release.

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
