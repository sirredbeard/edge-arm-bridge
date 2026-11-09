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

Setup installs FEX and the amd64 Edge build on the host, the same way edge-fex does it. Setup downloads the Edge package from Microsoft at install time. Nothing in this repo re-hosts the Edge binary.

The first run of Edge is the one manual step. A pre-written preferences file skips the first-run screens, the default browser prompt, and the tour. What it cannot skip is the Microsoft sign-in itself, and it should not: sign-in, sync, and any multi-factor prompt happen in the headed Edge window, once.

After that the service does the daily work. It waits until both browsers are closed, copies the Edge profile into a staging directory, moves the keyring secret, runs a decrypt check against a sample of the copied passwords and cookies, and only then swaps the staging copy in as the Chromium profile. The old Chromium profile stays as a dated backup. If any step fails, the live profile is left alone and the service writes status.json with the reason and exits with a code that says which step failed.

The mirror is one-way. Edge is the source of truth. Changes made in Chromium do not flow back, and Edge-only stores (Collections, the Edge wallet, Edge sessions) have no Chromium equivalent and do not transfer.

## Run it

Setup, on an arm64 Ubuntu host:

```bash
$ ./setup/setup.sh
```

Sign into Edge when it opens. Close Edge when sync finishes.

One manual sync:

```bash
$ dotnet run --project src/EdgeArmBridge -- --once
```

Dry run, which copies to staging and runs the checks but never swaps:

```bash
$ dotnet run --project src/EdgeArmBridge -- --once --dry-run
```

Daily sync under systemd:

```bash
$ cp systemd/edge-arm-bridge.service systemd/edge-arm-bridge.timer ~/.config/systemd/user/
$ systemctl --user enable --now edge-arm-bridge.timer
```

The timer fires once a day and catches up after a missed day. The service also runs fine by hand, which is the better way to watch the first few syncs.

## Repository layout

```text
edge-arm-bridge/
|-- findings/     # investigation notes and test records
|-- setup/        # host setup script and the experimental sign-in Containerfile
|-- src/          # the .NET 11 sync service
|-- systemd/      # user service and daily timer
|-- tools/        # profile probe used by the decrypt check
|-- LICENSE
`-- README.md
```

## Thanks

The FEX launch path, the child-process workaround, and the Wayland notes come from edge-fex. The profile copy experiments build on the agent-edge-bridge work on the same board.

## License

Apache 2.0. See LICENSE.
