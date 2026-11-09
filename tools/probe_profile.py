#!/usr/bin/env python3
"""Read-only probe of a Chromium-family profile directory.

Prints one JSON object: login and cookie counts, password blob prefixes,
and one sample password blob in hex for the caller's decrypt check.
The sample blob is encrypted. No plain text password is ever printed.
Usage: probe_profile.py <profile-dir>
"""
import json
import os
import sqlite3
import sys


def open_ro(path):
    return sqlite3.connect(f"file:{path}?mode=ro", uri=True)


def probe(profile):
    result = {"profile": profile, "logins": 0, "login_prefixes": {}, "cookies": 0,
              "cookie_prefixes": {}, "sample_login_blob_hex": "", "newest_mtime": 0}
    default = os.path.join(profile, "Default")
    login_data = os.path.join(default, "Login Data")
    if os.path.exists(login_data):
        con = open_ro(login_data)
        rows = con.execute("select password_value from logins").fetchall()
        result["logins"] = len(rows)
        for (blob,) in rows:
            if blob:
                prefix = blob[:3].decode("ascii", "replace")
                result["login_prefixes"][prefix] = result["login_prefixes"].get(prefix, 0) + 1
                if not result["sample_login_blob_hex"] and len(blob) > 10:
                    result["sample_login_blob_hex"] = blob.hex()
        con.close()
    for cookie_path in (os.path.join(default, "Network", "Cookies"), os.path.join(default, "Cookies")):
        if os.path.exists(cookie_path):
            con = open_ro(cookie_path)
            rows = con.execute("select encrypted_value from cookies").fetchall()
            result["cookies"] = len(rows)
            for (blob,) in rows:
                if blob:
                    prefix = blob[:3].decode("ascii", "replace")
                    result["cookie_prefixes"][prefix] = result["cookie_prefixes"].get(prefix, 0) + 1
            con.close()
            break
    for root, _dirs, files in os.walk(profile):
        for name in files:
            try:
                mtime = os.path.getmtime(os.path.join(root, name))
                result["newest_mtime"] = max(result["newest_mtime"], int(mtime))
            except OSError:
                pass
    return result


if __name__ == "__main__":
    print(json.dumps(probe(sys.argv[1])))
