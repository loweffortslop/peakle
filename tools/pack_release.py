"""Pack the built mod into a Thunderstore package zip (manifest.json, README.md, icon.png, the DLL at the root).

The zip can be uploaded to Thunderstore, or imported into a mod manager with "Import local mod".

usage: pack_release.py <built dll> <package dir> <out dir> <author>
"""
import json
import sys
import zipfile
from pathlib import Path

dll, package, out_dir = (Path(a) for a in sys.argv[1:4])
author = sys.argv[4]

manifest = json.loads((package / "manifest.json").read_text(encoding="utf8"))
name, version = manifest["name"], manifest["version_number"]
out_dir.mkdir(parents=True, exist_ok=True)
zip_path = out_dir / f"{author}-{name}-{version}.zip"
# Only the newest package is kept.
for old in out_dir.glob(f"{author}-{name}-*.zip"):
    if old != zip_path:
        old.unlink()

with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
    for f in ("manifest.json", "README.md", "icon.png", "CHANGELOG.md"):
        if (package / f).exists():
            z.write(package / f, f)
    z.write(dll, dll.name)

print(zip_path)
