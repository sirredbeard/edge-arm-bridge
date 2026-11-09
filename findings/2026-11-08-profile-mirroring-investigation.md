# Can an Edge profile be mirrored to arm64 Chromium, passwords included

Investigation date: November 8, 2026. Test board: Arduino VENTUNO Q, arm64, Ubuntu 24.04.

Question from the start: can a user download one project, sign into a Microsoft profile in the x86-64 Edge running under FEX, skip the rest of the Edge setup, and have a service keep a native arm64 Chromium profile in sync with it, passwords included. Answer: yes for the profile, with conditions. The conditions are the interesting part, and most of them sit in one place: the encryption key is not in the profile.

## Versions on the bench

- FEX: fex-emu-armv8.2, 2611-1, arm64 package
- Edge: microsoft-edge-stable 155.0.4283.45-1, amd64, inside the Ubuntu 24.04 FEX rootfs
- Chromium side: Google Chrome for Testing 155.0.8059.39, arm64 build
- Podman: 4.9.3
- .NET: SDK 11.0.100-rc.3
- Edge profile on this board: ~/.config/microsoft-edge, 576 MB

The two browsers sit on the same major version, 155. That alignment is luck, not a promise, and finding 8 covers what happens when it drifts.

## Finding 1: the profile files copy cleanly, and that proves almost nothing

A straight copy of the Edge profile opens in arm64 Chromium. This board already carries two copies made for the agent-edge-bridge work, and the copy of Default/Login Data is byte-identical to the Edge original (same md5, a38712d59079b2c968e0dc689d7eca12). The cookie host sets match too: 27 hosts in Edge, 27 in the copy, 26 shared at last check plus normal drift.

Chromium does not reject the foreign profile. It reads the parts it knows and ignores the Edge-only parts. That is the good news, and it is where the good news from file copying stops.

## Finding 2: passwords and cookies are encrypted with a keyring secret, not a profile key

On this Linux setup, Local State holds no os_crypt encrypted_key at all. That key is a Windows idea (DPAPI). Here the profile databases hold the encrypted values and GNOME Keyring holds the secret they were encrypted with.

Counts from the live Edge profile, read from the SQLite databases directly:

- Default/Login Data: 664 logins, 661 with a non-empty password blob, every one of those blobs starting with the bytes v11
- Default/Login Data For Account: 0 rows. The Microsoft account login store is empty on this board, so nothing is stranded there today.
- Default/Cookies (via Default/Network in newer layouts): 151 cookies, 140 with v11 blobs, 11 with v10 blobs

A v11 blob on Linux is AES-128-CBC. The key comes from PBKDF2-HMAC-SHA1 over the Safe Storage secret, salt 'saltysalt', 1 iteration, with a 16-space IV. I tested the two cheap guesses against a real login blob from this profile: the basic-store fallback password ('peanuts') and an empty password. Neither decrypts it. The secret is a real random value, and it lives in the keyring.

## Finding 3: the keyring is only readable inside an unlocked desktop session

GNOME Keyring on this board holds 5 items. Queried from a headless shell over the session bus, every item reports locked, with no label readable. That includes, by name, the entries Chromium-style browsers create for themselves: one per browser, named '<product> Safe Storage'.

Two consequences. First, the sync service has to run in the user's desktop session (a systemd user service qualifies) or it cannot move the secret at all. Second, when it cannot, it has to stop and say so, not copy the files anyway. A file copy without the secret is worse than no copy, per finding 4.

## Finding 4: in a Podman container, a copied profile loses it's cookies on first open

Test: copy the Edge profile to a scratch directory (a test copy only, deleted after, never committed), mount it and the arm64 Chrome for Testing build into an ubuntu:24.04 Podman container, open the copy headless. The container has no keyring, which stands in for any target that is missing the Edge secret.

Result:

- Before: Login Data 664 rows, Cookies 151 rows
- Chromium exit code: 0, page rendered
- Log line: Failed to decrypt token for service AccountId-..., from token_service_table.cc
- After: Login Data still 664 rows, Cookies 0 rows

Chromium kept the login rows as undecryptable blobs and discarded the cookies it could not decrypt. The browser looks healthy. The signed-in sessions the copy was meant to save are gone. Any sync tool that copies files and reports success on exit code alone will call this a good sync. It is the failure this project exists to avoid, so the service in src/ treats a cookie-count collapse in the staged copy as a hard failure and never swaps it in.

## Finding 5: with the same secret, the same blobs decrypt. The fix is moving one keyring entry

Control test, same container, no browser: encrypt a sample string as a v11 blob with one Safe Storage secret, then try three keys. The matching secret recovers the plain text. A different secret (the one a second browser would generate for itself) recovers nothing. 'peanuts' recovers nothing.

So the whole password question reduces to one move: read the secret in the 'Microsoft Edge Safe Storage' keyring entry and store the same value under the Chromium entry name ('Chromium Safe Storage', or the Chrome for Testing equivalent). Both browsers then derive the same AES key, and every copied v11 and v10 blob decrypts in the target. The secret moves keyring to keyring through the Secret Service API. It never lands on disk in plain text, and the service never logs it.

Edge case, stated plain: if the target browser already generated it's own secret and saved passwords with it, overwriting that entry strands the target's own saved passwords. On this board the Chromium side is a mirror and holds nothing of it's own, so the service proceeds, but it checks for existing target logins first and stops for a human decision if it finds any that did not come from the mirror.

## Finding 6: FEX inside Podman does not work yet. Setup stays on the host

The packaged-together idea was the attractive one: one container with FEX and Edge inside, nothing to install. Test: run the host FEX binary inside an ubuntu:24.04 Podman container with the existing rootfs mounted. FEX starts, then reports 'Couldn't execute: FEXServer' and 'the squashFS rootfs won't be mounted. Expect errors!'. The FEX server wants mount and device access a plain rootless container does not have.

A privileged container with the right devices might get further. That trades away the simple download-and-run story, so setup/setup.sh installs FEX and Edge on the host, the route edge-fex already proved on this board. setup/Containerfile.signin keeps the container attempt in the repo as an experiment, marked as such.

## Finding 7: the Edge binary is downloaded at setup time, not bundled

Nothing in this repo carries the Edge binary, the FEX rootfs, or a browser build. Setup pins and downloads each piece from it's own source: Edge from packages.microsoft.com, FEX from it's Ubuntu packages, Chrome for Testing from Google's download bucket. The repo stays a handful of text files, the versions stay explicit in setup.sh, and nobody re-hosts a browser by accident.

Rootfs size note: the FEX rootfs with Edge installed is 2.9 GB on this board. Any future all-in-one download is a multi-GB download whatever shape it takes. That is a fact to design around, not a detail.

## Finding 8: version drift is the long-term risk, not the copy

Edge 155 and Chrome for Testing 155 share a profile schema today. Chromium profile formats change between major versions, and Edge adds fields Chromium ignores (harmless) while Chromium occasionally changes a table Edge also writes (not harmless). Rules the service follows:

- Read the source version from the profile's Last Version file and the target browser version from the binary. If the target major is older than the source major, stop. Do not swap.
- Keep a dated backup of the previous Chromium profile on every swap, so a bad schema day is a restore, not a loss.
- Treat a sudden drop in login or cookie counts in the staged copy as failure, whatever the exit codes said (finding 4).

## Finding 9: stale Edge sync is a separate failure from a broken mirror

If Edge itself stops syncing with Microsoft (expired sign-in, a sync error Edge shows only in it's own UI), the mirror will faithfully copy a stale profile and every check in this repo will pass. The service watches for the smell of it: if the newest file in the Edge profile and the Login Data mtime have not moved across two runs, and the machine was in use, it writes a 'source may be stale' warning into status.json and exits 2 instead of reporting a clean sync. It cannot fix Edge's sign-in. It can refuse to call the result healthy.

## What does not transfer

- Microsoft sync itself. Chromium cannot sign into a Microsoft account. The mirror is a snapshot, refreshed daily, not a sync client.
- Edge-only stores: Collections, Edge Wallet data, Edge Sessions, the sidebar state. Chromium ignores the files. Nothing breaks, nothing arrives either.
- Anything in Login Data For Account, if a future Edge build starts using it on Linux. It is empty here. The service copies the file, but whether Chromium reads account logins from it is untested, because there is nothing to test with.
- Changes made in Chromium. One-way only, by design.

## Still untested, in order

1. The keyring move end to end in a live desktop session, followed by a password auto-fill in Chromium on a real site. Findings 2 and 5 say it works. It has not been watched working yet.
2. A week of daily swaps, watching for profile corruption or slow drift in counts.
3. Edge and Chromium on different major versions, to see the version gate in finding 8 fire for real.
4. The privileged-container route for FEX, if the host install ever becomes the thing blocking people.

## Service test, same day

The sync service in src/ was built with the .NET SDK 11.0.100-rc.3 on this board (0 errors) and run against a synthetic profile: a Login Data and Cookies database built for the test, holding one v11 blob encrypted with a known test secret. Three runs:

- Dry run, correct secret through the environment: exit 0, staging built, decrypt check passed, nothing swapped
- Full run, correct secret: exit 0, target profile created, marker file written, status.json reports decrypt_check passed
- Full run, wrong secret: exit 3 (decrypt check failed), target left untouched

That is the finding 4 failure, caught by the service before a swap instead of by a user after one.
