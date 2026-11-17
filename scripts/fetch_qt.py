"""Fetch the Qt 6.12.0 MSVC 2022 x64 packages RPCS3 needs, straight from download.qt.io.

aqtinstall 3.3.0 does not know the per-architecture repository layout used by Qt 6.12,
so this reads Updates.xml itself. Usage: python fetch_qt.py <output_dir>
"""
import hashlib
import sys
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

import py7zr

BASE = "https://download.qt.io/online/qtsdkrepository/windows_x86/desktop/qt6_6120/qt6_6120_msvc2022_64/"
PACKAGES = {
    "qt.qt6.6120.win64_msvc2022_64": ("qtbase", "qtsvg", "qtdeclarative", "qttools"),
    "qt.qt6.6120.addons.qtmultimedia.win64_msvc2022_64": ("qtmultimedia",),
}


def fetch(url: str) -> bytes:
    with urllib.request.urlopen(url, timeout=120) as r:
        return r.read()


def main() -> None:
    out = Path(sys.argv[1]).resolve()
    cache = out / "_archives"
    cache.mkdir(parents=True, exist_ok=True)
    root = ET.fromstring(fetch(BASE + "Updates.xml"))
    for pkg in root.iter("PackageUpdate"):
        name = pkg.findtext("Name")
        if name not in PACKAGES:
            continue
        version = pkg.findtext("Version")
        archives = [a.strip() for a in (pkg.findtext("DownloadableArchives") or "").split(",") if a.strip()]
        for archive in archives:
            if not archive.startswith(PACKAGES[name]):
                continue
            url = f"{BASE}{name}/{version}{archive}"
            target = cache / archive
            if not target.exists():
                print(f"downloading {archive}", flush=True)
                data = fetch(url)
                expected = fetch(url + ".sha1").decode().split()[0]
                if hashlib.sha1(data).hexdigest() != expected:
                    raise SystemExit(f"checksum mismatch for {archive}")
                target.write_bytes(data)
            print(f"extracting {archive}", flush=True)
            with py7zr.SevenZipFile(target) as z:
                z.extractall(out)
    print("done", flush=True)


if __name__ == "__main__":
    main()
