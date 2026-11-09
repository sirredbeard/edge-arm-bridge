#!/usr/bin/env python3
"""Build a trimmed x86-64 sysroot for Edge under FEX.

Usage: mk-sysroot.py <pool-dir> <edge-dir> <out-dir>

<pool-dir> is a directory of extracted amd64 .deb trees (dpkg-deb -x output
merged into one tree). <edge-dir> is the extracted Edge tree's msedge
directory. The walker starts from every file in the Edge directory, follows
DT_NEEDED entries through the pool, and copies only the closure. Libraries
that Chromium and NSS open with dlopen are not in any DT_NEEDED list, so
they are seeded by name in DLOPEN_SEEDS and MODULE_DIRS below. The first
version of this script learned each of those the hard way: a missing
libsoftokn3.so is a FATAL NSS abort, not a loader error.

Layout notes, also learned the hard way:
- The sysroot is usr-merged. /lib must be a symlink to usr/lib, and the
  x86-64 loader must be reachable at /lib64/ld-linux-x86-64.so.2, or FEX
  rejects the rootfs with 'Invalid or Unsupported elf file'.
- A provider package does not always start with 'lib'. zlib1g ships
  libz.so.1. The build script's package list accounts for that.
"""
import os
import shutil
import subprocess
import sys

LIBDIRS = ["usr/lib/x86_64-linux-gnu", "lib/x86_64-linux-gnu", "usr/lib", "lib"]

DLOPEN_SEEDS = [
    # NSS: Chromium initialises NSS at startup, and NSS dlopens these.
    "libsoftokn3.so", "libfreebl3.so", "libfreeblpriv3.so", "libnssdbm3.so",
    "libnssckbi.so", "libsqlite3.so.0",
    # Graphics and windowing libraries FEX thunks and Chromium dlopen.
    "libEGL.so.1", "libGL.so.1", "libGLESv2.so.2", "libvulkan.so.1",
    "libwayland-client.so.0", "libwayland-egl.so.1", "libgallium-24.0.9.so",
    # GTK is on the Depends list for the headed browser.
    "libgtk-3.so.0", "libgdk-3.so.0", "libcurl.so.4",
    "libfontconfig.so.1", "libfreetype.so.6", "libharfbuzz.so.0",
]

# Versioned module directories that loaders search by path, not by soname.
MODULE_DIRS = ["gtk-3.0", "gdk-pixbuf-2.0", "pango", "gio"]

ETC_FILES = ["etc/passwd", "etc/group", "etc/hosts", "etc/resolv.conf", "etc/localtime"]
SHARE_DIRS = ["usr/share/fonts", "usr/share/fontconfig", "usr/share/mime", "etc/fonts", "etc/ssl", "usr/lib/ssl"]


def needed(path):
    try:
        out = subprocess.run(["readelf", "-d", path], capture_output=True, text=True).stdout
    except Exception:
        return []
    return [line.split("[")[1].rstrip("]") for line in out.splitlines() if "(NEEDED)" in line]


# Trims applied to the Edge tree as it lands in the sysroot. This image is
# a sign-in appliance, not a general browser install. Each entry was tested
# the same way: remove it, render https://example.com and the Microsoft
# sign-in page headless, read the log for FATAL and loader errors.
# Locales are trimmed separately: en-US only, which takes locales/ from
# 146 MB to 1.2 MB on Edge 157.
EDGE_TRIMS = [
    "WidevineCdm",               # DRM playback. A sign-in browser never loads it.
    "msedge-management-service", # Enterprise management binary.
    "libmspdf.so",               # PDF viewer, loaded on demand by PDFs.
    "libonnxruntime.so",         # On-device ML for browser features.
    "libmip_core_gn.so",         # Data protection library.
    "liboneds.so",               # OneDrive/DS integration.
]


def trim_edge(edge_out):
    locales = os.path.join(edge_out, "locales")
    if os.path.isdir(locales):
        for name in os.listdir(locales):
            if not name.startswith("en-US"):
                os.remove(os.path.join(locales, name))
    for name in EDGE_TRIMS:
        path = os.path.join(edge_out, name)
        if os.path.isdir(path):
            shutil.rmtree(path)
        elif os.path.exists(path):
            os.remove(path)


def main():
    pool, edge_dir, out = sys.argv[1], sys.argv[2], sys.argv[3]

    def find_lib(name):
        for d in LIBDIRS:
            p = os.path.join(pool, d, name)
            if os.path.exists(p):
                return p
        p = os.path.join(edge_dir, name)
        return p if os.path.exists(p) else None

    if os.path.exists(out):
        shutil.rmtree(out)
    os.makedirs(os.path.join(out, "opt/microsoft"))
    shutil.copytree(edge_dir, os.path.join(out, "opt/microsoft/msedge"), symlinks=True)
    trim_edge(os.path.join(out, "opt/microsoft/msedge"))

    lib_out = os.path.join(out, "usr/lib/x86_64-linux-gnu")
    os.makedirs(lib_out)

    def copy_lib(src):
        name = os.path.basename(src)
        dst = os.path.join(lib_out, name)
        if os.path.islink(src):
            if not os.path.lexists(dst):
                os.symlink(os.readlink(src), dst)
            real = os.path.realpath(src)
            if os.path.exists(real):
                real_dst = os.path.join(lib_out, os.path.basename(real))
                if not os.path.exists(real_dst):
                    shutil.copy2(real, real_dst)
                return real
            print(f"DANGLING {name}: target missing from pool")
            return None
        if not os.path.exists(dst):
            shutil.copy2(src, dst)
        return src

    seeds = []
    for f in os.listdir(edge_dir):
        p = os.path.join(edge_dir, f)
        if os.path.isfile(p) and not os.path.islink(p):
            seeds.append(p)
    for name in DLOPEN_SEEDS:
        p = find_lib(name)
        if p:
            copied = copy_lib(p)
            if copied:
                seeds.append(copied)
        else:
            print(f"SEED MISSING {name}")

    seen = set()
    queue = list(seeds)
    while queue:
        cur = queue.pop()
        for name in needed(cur):
            if name in seen:
                continue
            seen.add(name)
            src = find_lib(name)
            if not src:
                # Qt shows up here from Edge helper libraries. Edge runs
                # without it in every test so far. Report, do not fail.
                print(f"MISSING {name}")
                continue
            copied = copy_lib(src)
            if copied:
                queue.append(copied)

    for d in MODULE_DIRS:
        src = os.path.join(pool, "usr/lib/x86_64-linux-gnu", d)
        if os.path.isdir(src):
            shutil.copytree(src, os.path.join(lib_out, d), symlinks=True)

    # usr-merged layout: /lib points at usr/lib, loader at /lib64.
    os.symlink("usr/lib", os.path.join(out, "lib"))
    os.makedirs(os.path.join(out, "lib64"))
    loader = find_lib("ld-linux-x86-64.so.2")
    if loader:
        real = os.path.realpath(loader)
        shutil.copy2(real, os.path.join(lib_out, "ld-linux-x86-64.so.2"))
        os.symlink("../lib/x86_64-linux-gnu/ld-linux-x86-64.so.2", os.path.join(out, "lib64/ld-linux-x86-64.so.2"))

    # /bin/sh: Edge's child-process fallback reaches for it.
    os.makedirs(os.path.join(out, "bin"))
    dash = os.path.join(pool, "usr/bin/dash")
    if os.path.exists(dash):
        shutil.copy2(dash, os.path.join(out, "bin/dash"))
        os.symlink("dash", os.path.join(out, "bin/sh"))

    for rel in ETC_FILES:
        src = os.path.join(pool, rel)
        if os.path.exists(src):
            dst = os.path.join(out, rel)
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            shutil.copy2(src, dst)
    nsswitch = os.path.join(out, "etc/nsswitch.conf")
    if not os.path.exists(nsswitch):
        os.makedirs(os.path.dirname(nsswitch), exist_ok=True)
        with open(nsswitch, "w") as f:
            f.write("passwd: files\ngroup: files\nhosts: files dns\n")
    for rel in SHARE_DIRS:
        src = os.path.join(pool, rel)
        dst = os.path.join(out, rel)
        if os.path.isdir(src) and not os.path.exists(dst):
            shutil.copytree(src, dst, symlinks=True)
    xkb = os.path.join(pool, "usr/share/X11/xkb")
    if os.path.isdir(xkb):
        dst = os.path.join(out, "usr/share/X11")
        os.makedirs(dst, exist_ok=True)
        shutil.copytree(xkb, os.path.join(dst, "xkb"), symlinks=True)

    print(f"sysroot written to {out}: {len(seen)} DT_NEEDED entries resolved")


if __name__ == "__main__":
    main()
