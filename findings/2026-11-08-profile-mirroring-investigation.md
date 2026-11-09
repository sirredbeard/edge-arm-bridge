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

## Round two, same day: the bundle, no package manager on the user's machine

Direction change: drop Podman from the testing, and drop the apt-on-Ubuntu versus dnf-on-Fedora problem with it. The new shape is one tar. The tar carries extracted binaries for FEX and Edge, a trimmed x86-64 sysroot, an arm64 Chromium, and the sync service. A user untars it and runs a script. A build script on a build machine does the downloading and extracting, and a workflow can rebuild the tar on a schedule so the versions inside stay current.

## Finding 10: FEX runs fine extracted, never installed

The FEX GitHub release FEX-2611 carries no binary assets at all, so the binary source is the PPA .deb: fex-emu-armv8.2_2611-1~n_arm64.deb, 3,382,712 bytes, downloaded straight from ppa.launchpadcontent.net. Extracted with dpkg-deb -x, the tree is 20 MB.

The part that matters: /usr/bin/FEX and /usr/bin/FEXServer link only libc, libstdc++, libm, and libgcc. The long Depends list on the .deb (Qt5, zenity, squashfuse) belongs to FEXConfig and the rootfs fetcher, not to the emulator. The bundle drops those two binaries and the Qt question goes with them.

Test: staged FEX from the extracted tree, on PATH, against the board's existing rootfs and Edge 155. Headless about:blank and https://example.com both rendered, exit 0. FEXServer starts from the sibling binary without an install.

## Finding 11: latest has already moved. The pairing rule is not optional

The board's Edge is 155.0.4283.45-1. The Microsoft pool's newest stable on November 8, 2026 is 157.0.4341.42-1, a 201,183,074 byte .deb. Chrome for Testing's latest stable the same day is 157.0.8103.38. The majors pair up again at 157, and that pairing is luck each cycle, so build-bundle.sh reads both versions at build time and fails the build if the majors differ, rather than shipping a bundle the version gate in the sync service would refuse to use.

## Finding 12: a trimmed sysroot is 750 MB, and 680 MB of it is Edge

The full FEX rootfs on this board is 2.9 GB: 1.4 GB of usr/lib, 392 MB of var, 287 MB of usr/bin, and Edge itself at 651 MB. Walking the DT_NEEDED closure from the Edge binaries through the rootfs libraries resolves 117 entries and produces a sysroot of 746 MB, of which the extracted Edge 157 tree is 680 MB. Everything else Edge needs to start fits in roughly 70 MB.

Three lessons from that walk, each paid for once:

- The sysroot is usr-merged or FEX rejects it. /lib must be a symlink to usr/lib and the loader must answer at /lib64/ld-linux-x86-64.so.2. Without that, FEX prints 'Invalid or Unsupported elf file' and 'misconfigured x86-64 RootFS'.
- DT_NEEDED is necessary and not sufficient. NSS dlopens libsoftokn3.so and friends at startup, and a miss there is a FATAL abort in nss_util.cc, not a loader error. GTK, gdk-pixbuf, pango, and gio load whole module directories by path. mk-sysroot.py seeds both lists by name.
- Qt sonames (libQt5Core, libQt6Core, and family) show up in the closure from Edge helper libraries and resolve nowhere, including in the full rootfs. Edge runs without them in every test here. The walker reports them and moves on.

Result: staged FEX, trimmed sysroot, Edge 157, https://example.com rendered, exit 0, no loader errors, no FATAL lines.

## Finding 13: the sysroot builds from downloaded .debs alone, no installed rootfs

The release build cannot depend on a rootfs somebody built by hand, so the same sysroot was rebuilt with a scratch apt state (it's own lists and cache under the build directory, system apt untouched) against Ubuntu noble amd64 plus the Microsoft repo. apt-cache depends --recurse on microsoft-edge-stable returns 228 packages. Filtered to library packages plus a short extras list, 176 packages downloaded, about 182 MB of .debs, 302 MB extracted.

The filter is where the traps are:

- Providers do not all start with 'lib'. zlib1g ships libz.so.1, and the first pool build died on exactly that: 'libz.so.1: cannot open shared object file'. The extras list now names zlib1g, dash, mesa-libgallium, xkb-data, shared-mime-info, fontconfig, and ca-certificates.
- The pool is usr-merged in a different shape than the installed rootfs (the loader sits at usr/lib64 in the pool). mk-sysroot.py normalizes the layout instead of copying it, which is why finding 12's /lib symlink rule is in the script and not in this paragraph only.
- etc/nsswitch.conf comes from no library package. The script writes a three-line one.

Result: pool-built sysroot, 784 MB with the mesa and xkb data included. Staged FEX and Edge 157 rendered https://example.com, exit 0. The chain from .deb downloads to a running Edge never installs a package and never touches the host's apt state.

## Finding 14: the assembled bundle, untarred cold, works

A bundle was assembled the way build-bundle.sh assembles it: FEX tree 20 MB, sysroot from finding 13, Chrome for Testing 157.0.8103.38 arm64 (203 MB zip, 407 MB extracted), the bridge service as a self-contained linux-arm64 publish (87 MB with the runtime, .pdb files stripped in real builds), tools, install scripts, and VERSIONS.txt. 1.3 GB untarred. The tar.gz is 555,266,935 bytes.

Cold test, scratch HOME, nothing pre-installed from the project:

- install.sh: exit 0. It ldd-checks the FEX and Chromium binaries against host libraries, writes config.json with the untarred bundle's real paths, and writes the systemd user units with the bundle path filled in.
- The untarred FEX, sysroot, and Edge 157, pointed at the fresh profile directory install.sh created: rendered https://example.com, exit 0, and Edge created it's own profile files in that directory (Default/Login Data present after the run).

What install.sh does not do is install host libraries. FEX and Chromium link a short list of host libraries (libstdc++ and the graphics stack). A desktop has them. Where they are missing, install.sh names them and stops. That is the honest edge of 'no package manager': the bundle carries everything that is project's to carry, and says plainly what it cannot.

## Finding 15: what is left on the host, and what is still untested

Left on the host by design: a glibc, a desktop session with an unlocked keyring (finding 3), systemd user services for the daily timer, and python3 for the profile probe. The bundle binaries are Ubuntu noble builds. Fedora arm64 is also glibc, and the ldd check is the test that decides it machine by machine. Fedora itself is untested: nobody has untarred this bundle on a Fedora host yet.

Still untested in this round: a headed Wayland run from the bundle sysroot (the GTK and thunk surface is seeded, not proven), the GitHub workflow in .github/workflows/build-bundle.yml (scaffold, mirrors the tested build script), and a rebuild cycle against a real new Edge major.

## Round three, same day: self-contained FEX, a trimmed image, and one script

Direction for this round: the bundle is three things - the compiled sync app, a self-contained FEX that does not need a host install, and the FEX image (an optimized rootfs carrying a recent Edge). Installation becomes one script with three modes: --install pulls the newest release tar, --configure opens Edge for the Microsoft sign-in, --service sets up a systemd timer that wakes Edge under FEX on a cycle, lets it sync, and mirrors the profile to Chromium.

## Finding 16: FEX can carry it's own C runtime. One path has to be patched at install time

FEX and FEXServer link libc, libstdc++, libm, and libgcc (finding 10). Extract the arm64 builds of libc6, libstdc++6, and libgcc-s1 into the same tree and the dependency set is complete for the emulator itself. The binaries then need two path settings:

- An rpath of $ORIGIN/../lib/aarch64-linux-gnu. DT_RUNPATH expands $ORIGIN, so this works wherever the bundle lands. Set at build time.
- The interpreter (PT_INTERP). That path is absolute and does not expand anything, so it cannot be set at build time. install.sh sets it on the user's machine, running the bundled patchelf through the bundled loader: loader patchelf --set-interpreter <bundle loader> FEX. No host tools involved.

Tests, all with env -i (no PATH to the staged tree, no LD_LIBRARY_PATH):

- Patched tree in place: Edge rendered https://example.com, exit 0.
- Tree copied to a new location and re-patched there, original tree hidden: Edge rendered about:blank, exit 0. The bundle survives being moved, which is the whole point.
- The FEX tree with runtime libraries included is 46 MB.

The graphics thunks are the exception that proves the rule. HostThunks dlopen the host's GL and Wayland libraries on purpose: the whole idea is to reach the host GPU. Distro agnostic covers the emulator's own runtime. The guest still renders through the host's graphics stack, as it did in edge-fex.

## Finding 17: the Edge image trims from 749 MB to 538 MB, and the sign-in page still renders

The Edge 157 tree is 680 MB, and 402 MB of that is the msedge binary itself. The rest was negotiable:

- locales/: 336 files, 146 MB. Keeping en-US only (four files, 1.2 MB) saves 145 MB. A user who needs another language needs that language's .pak files back: the trim list in mk-sysroot.py is one constant, and this is the first entry to revisit for a non-English build.
- WidevineCdm (21 MB), libmspdf.so (17 MB), libonnxruntime.so (12 MB), libmip_core_gn.so (7.7 MB), msedge-management-service (6.9 MB), liboneds.so (3.4 MB): each removed, then the image retested.
- Kept on purpose: liboneauth.so, the Microsoft account authentication library. Removing parts near it would be self-defeating in a browser who's job is a Microsoft sign-in.

Every trim round ran the same checks against the self-contained FEX and the trimmed image: https://example.com rendered, no FATAL lines, no loader errors. Final check, and the one that matches the mission: https://login.microsoftonline.com/ rendered in full, 73,640 bytes of DOM, 'Sign in to your account' present. Sysroot after trims: 538 MB, against 749 MB untrimmed and 2.9 GB for the full rootfs.

One wart, recorded plain: during the headless wake run, one Edge child process logged 'LaunchProcess: failed to execvp: /opt/microsoft/msedge/msedge'. The browser itself ran, built the profile, and exited clean under the scope stop. Which child it was is not pinned down.

## Finding 18: the image ships as one squashfs file, 219 MB

The trimmed sysroot as a zstd squashfs (mksquashfs -comp zstd -b 1M) is 219,459,584 bytes. FEX mounts it through FEXServer when FEX_ROOTFS names the file: tested, Edge rendered https://example.com from the image, exit 0.

Two consequences worth stating. The image is read-only, so Edge cannot update it's own install inside it: for a sign-in appliance that is a feature, and a new Edge means a new bundle build, which the workflow exists to produce. And mounting needs /dev/fuse on the host: install.sh checks for it and says so in plain language, and build-bundle.sh can still produce the loose directory form.

## Finding 19: the three-mode script, cold-tested end to end

Bundle v2 as assembled for the test: 749 MB untarred (against 1.3 GB for v1, the squashfs and the trims doing the work), tar.gz 472,561,821 bytes.

- --install from a local tarball with a scratch HOME: exit 0. Bundle landed in ~/.local/opt/edge-arm-bridge, and readelf on the installed FEX shows the interpreter pointing into that install directory, so the finding 16 patch chain works through the real script, not just by hand.
- --configure in a session with no display: exit 1 with a plain message to run it from the desktop session. The headed path itself is the round-one run-edge.sh, unchanged.
- --service: wrote edge-arm-bridge-wake.service and .timer with the install path filled in (OnCalendar 00,06,12,18:15). In the test shell systemctl --user could not be reached, and the script said so and printed the two commands instead of failing.
- Wake-and-mirror by hand: Edge woke headless from the squashfs image, built a fresh profile's worth of files, took the scope stop, and the mirror copied the profile to the Chromium directory and wrote it's marker file. status.json: synced, exit 0. A brand-new profile has no Login Data yet (no password has ever been saved in it), and the probe reported 0 logins rather than inventing a problem.

Addendum, after publishing: release v0.1.0 is live with the tar, it's sha256, and edge-arm-bridge.sh as assets. The uploaded tar was downloaded back through gh and the sha256 matches the local build (4f57225d...). One catch, recorded rather than fixed: this repo is private, and GitHub does not serve release assets to anonymous requests, so the curl one-liner returns 404 without a token today. The script's own --install uses the same public URL. Making the one-liner work for anyone means either making the repo public, or teaching --install to use an authenticated download (gh or a token in the URL's place). That is a decision about the repo, not a bug in the bundle, and it is left to the repo owner.

## Round four, same day: public repo, a system Chromium, and CI builds on release

## Finding 20: the repo is public, and the one-liner works as written

At the repo owner's direction the repo went public. The anonymous download that returned 404 in the round three addendum now returns the asset, and the full line was run on the board for real: curl the script, bash --install, the newest release tar comes down, the checksum verifies, the bundle installs into ~/.local/opt/edge-arm-bridge. Exit 0.

## Finding 21: on this board, system Chromium means Google Chrome, and the versions line up on their own

Ubuntu noble on arm64 offers chromium-browser only as a snap transitional package, and this board has no snap at all (no snap command, snapd inactive). The working system install is Google's own arm64 .deb of google-chrome-stable, which is the Chromium codebase with Google's branding and the same profile format. The copy installed here reports 157.0.8103.38: the same version as the Chrome for Testing build the bundle carries, because both come off the same release. The mirror config on the board now names /usr/bin/google-chrome as the target browser, with the target profile in a test directory. A wake-and-mirror cycle against it wrote 'synced, exit 0', and google-chrome opened the mirrored profile headless without complaint.

For other machines: any Chromium-family binary works as the target (chromiumBinaryPath in config.json). The version gate pairs majors between the Edge profile and the target browser, so a distribution Chromium a major behind Edge will trip exit code 6 until it catches up. That is the gate doing it's job.

## Finding 22: GitHub Actions builds the bundle on release, in about three minutes

The workflow runs on ubuntu-24.04-arm, sets up .NET 11, adds the FEX PPA for download only, installs squashfs-tools and patchelf, and calls packaging/build-bundle.sh, the same script tested locally. It then uploads the tar, it's sha256, and edge-arm-bridge.sh to the release with --clobber. Triggers: a release being published, or a manual dispatch naming a tag.

Two runs against v0.1.0, both success (runs 42546536610 and 42546926837). The CI bundle picked the same latest versions as the local build (FEX 2611, Edge 157.0.4341.42, Chrome for Testing 157.0.8103.38) and came out at 454 MB against the local 472 MB, the difference being gzip's default level against a -1 used by hand. The board was then wiped to the CI bundle through the public curl line, and a wake-and-mirror cycle ran clean on it.

One real bug was found and fixed between the two runs: the first CI checksum file named the tar by it's absolute build path, so sha256sum -c on a user's machine would have looked for a file that does not exist there and failed the install at the verify step. build-bundle.sh now writes the checksum with the plain filename. Worth remembering as the general shape of CI-only bugs: the build was fine, the artifact's paperwork was not, and only an end-to-end install from the release caught it.

The remaining flow note: --install always takes the newest published release, so a release published before it's workflow finishes has a short window with no bundle asset on it. Publish as a draft, let the build attach the bundle, then publish. The workflow file says the same.

## Round five, same day: the sync service as Native AOT

Direction for this round: the arm64 build of the service should be self-contained, trimmed, and fast on the current .NET 11 optimization paths, with no runtime on the machine at all. Guidance came from the dotnet/skills repo (github.com/dotnet/skills), in particular the dotnet-aot-compat skill: turn IsAotCompatible on, let the analyzers list the problems, fix what they point at, and use source-generated JSON where the serializer warnings cluster.

## Finding 23: Native AOT takes the service from 87 MB and a runtime to a 4 MB native binary

The project now sets PublishAot, IsAotCompatible, OptimizationPreference=Speed, and InvariantGlobalization. The publish is a single stripped ELF for linux-arm64, 4,028,928 bytes. The self-contained publish it replaces was 87 MB of runtime and framework. Process start on the usage path measured 83 ms for the self-contained build and 18 ms for the AOT binary on this board. The build machine needs clang and zlib1g-dev for the native link; both are on the board, and the workflow installs them.

The analyzers found exactly the expected problem class and nothing else. Three JsonSerializer calls used reflection: the config load, and the two small files the service writes (status.json and the sync state file). The config load and the state write moved to a source-generated JsonSerializerContext. The status write looked just as simple and was not:

## Finding 24: a Dictionary<string, object> is a polymorphic trap, and the test suite is what catches it

The first AOT build compiled clean and then died at runtime the first time it wrote status.json: no metadata for System.Int64, then for System.Boolean, because a dictionary of object holds whatever runtime types land in it and the generated serializer knows none of them. The fix is typed records (StatusFile, StateFile) with the same snake_case JSON names, written by the same code paths. status.json comes out in the same shape as before, nulls omitted as before.

The behavioral suite then passed end to end against the AOT binary, on the same synthetic profiles as round one: correct secret mirrors and exits 0 with decrypt_check passed in status.json, wrong secret exits 3 and does not swap, dry run exits 0 and swaps nothing. The lesson worth keeping is the order of events: build warnings said the code was AOT-clean, and it was, for compilation. Only running the real paths showed the dictionary problem. An AOT conversion is not done when it builds. It is done when the test suite passes against the native binary.

## Finding 25: GitHub Pages caps one file at 100 MB, so the Edge image ships in parts

The RPM stream follows the github-pages-rpm-repo template: a static DNF repository on GitHub Pages. Pages refuses any single file over 100 MB and caps the whole site at 1 GB. The Edge image is 219 MB as a squashfs and does not compress further, so one RPM holding it can never deploy. The image instead ships split across subpackages of at most 84 MB of payload each (three today). The main package requires every part, and its install script joins them back into the squashfs and checks the sha256 before anything uses it. The full set is about 227 MB, well inside the site cap.

## Finding 26: %post runs before the rest of the transaction lands; %posttrans is the hook that sees everything

The first RPM build joined the image parts in the main package's %post script. It produced no image and no useful error: in a multi-package transaction, each package's %post can run before sibling packages have laid their files down, and the main package sorts first. The fix is %posttrans, which runs once at the end of the transaction with every package in place. Assembly and hash checking live there now.

## Finding 27: a stray dollar sign emptied the stored hash, and one check passed anyway because empty equals empty

The generated spec wrote the expected image hash into the package from a placeholder token. The token kept a dollar sign in front of it, so the shell expanded it as a variable name and the installed hash file came out empty. Assembly then refused to run, correctly. The uncomfortable part is that one test round reported the image hash as matching: it compared a failed hash computation (empty output) against the empty file and called that a match. The check now asserts the assembled file exists and is not empty before any hash comparison. Verify the artifact, then verify its hash, in that order.

## Finding 28: with a fixed install path, patch the FEX interpreter at build time

The tar bundle patches the FEX interpreter at install time because its install path varies by user. An RPM always lands in /usr/lib/edge-arm-bridge, so the build patches the interpreter there ahead of time and no scriptlet touches the binaries. This route is also sturdier: the bundled arm64 loader, run inside a Fedora container to drive the bundled patchelf, dies with a bus error, while the build host's own patchelf does the job without drama. Install-time patching stays in the tar flow, where the path is not known until install.

## Finding 29: the GTK configurator is also Native AOT, one 3.9 MB binary

The settings app (src/EdgeArmBridge.Configurator, GirCore Gtk-4.0 bindings) publishes with the same AOT settings as the service: one native binary, no runtime, no IL warnings. It edits the same config file the service reads, opens the Edge sign-in window, runs a sync or a wake on request, shows the last sync result from status.json, and toggles the wake timer. GirCore generates its interop at build time, which is what keeps the AOT build clean.

## Finding 30: the package set installs clean on the real Azure Linux desktop userland

The acceptance test ran inside the Azure Linux desktop's own ARM live rootfs (CI run 42563762250 of sirredbeard/azurelinux-desktop, imported as a container image): dnf installed all five packages with no dependency work, the image parts joined to the expected hash, FEX executed, the sync service ran, and every library the configurator needs resolved. A plain Fedora 45 container passes the same checks. The azurelinux-desktop canary container was not usable for this test on the board: it composes an x86_64 userland by design, and its package set does not resolve for aarch64.

## Finding 31: the bundle build takes its output directory from BUILD_DIR and ignores positional arguments

The first two runs of the Pages workflow failed at the RPM step with "no Edge image" because the workflow passed the output directory as an argument and mounted the path it expected. build-bundle.sh never reads its arguments: BUILD comes from the BUILD_DIR environment variable, defaulting to build/ inside the repo, and the staging tree is $BUILD/bundle. The workflow now sets BUILD_DIR and mounts dist/bundle. Worth writing down because the failure looks like a packaging bug and is not: the bundle was built correctly both times, in a directory nothing was watching.

## Finding 32: the configurator now carries a first run end to end

Since the bridge ships inside the Azure Linux ARM64 image, the first run a user gets is the desktop entries, not a terminal. The configurator opens with a first run card when no successful sync is on record: step one opens Edge and watches for the profile's processes, and when the window closes the mirror starts on its own; step two narrates the mirror in plain words (copy, keyring move, decrypt test) and reports the outcome from status.json, with the exit codes translated (3 is a decrypt failure and nothing swapped, 4 is a profile lock, 5 is a missing keyring secret, 6 is the version gate); step three opens the target Chromium with the mirrored profile once the mirror lands. Any step can also be run by hand, and the card hides itself once a sync has succeeded. The settings grid below it is unchanged.

## Finding 33: the service registers itself, and the mirror demands a landing browser

Two packaging changes at Hayden's direction. First, the wake timer is on by default: the package ships a user preset (90-edge-arm-bridge.preset) and, on first install only, %post runs systemctl --global enable, which is how a package turns a user unit on for every user. The unit stays a user unit on purpose: the sync needs the user's keyring and session bus, so a root system service could not do the job. The settings app is the off switch (a per-user disable beats the global enable), and upgrades never re-enable, because the scriptlet only runs when $1 is 1. Verified in the Fedora container: the timers.target.wants symlink lands in /etc/systemd/user at install.

Second, no Chromium means an error, at both levels. The main package now requires (chromium or google-chrome-stable), so dnf refuses the install when neither is available (verified by excluding chromium: the transaction fails naming the requirement). The configurator checks for the configured browser binary at startup too, and says in plain words that the mirror has nowhere to land, with the mirror step guarded. The sync icon was redrawn at the same time: a Chromium orb and an Edge orb joined by circular sync arrows on the Edge gradient, verified pixel by pixel against a librsvg render (the Chromium wedges are exact thirds in the brand colors).
