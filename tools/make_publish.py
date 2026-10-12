"""Assemble what gets published, from an allowlist (nothing else in the project can slip in).

    publish/github/       the source repository: plugin source, package files, build tools, art, docs
    publish/thunderstore/ the Thunderstore package (manifest, README, changelog, icon, DLL) and its zip

Decompiled game code (_decomp/), build output, the dev notes and the reference dump scripts are never copied.
Edit the sources (README.md, package/README.md, package/CHANGELOG.md, ...), then run this again.

usage: python tools/make_publish.py [built dll]   (default: src/Peakle/bin/Release/Peakle.dll)
"""
import json
import shutil
import sys
import zipfile
from pathlib import Path

root = Path(__file__).resolve().parent.parent
dll = Path(sys.argv[1]) if len(sys.argv) > 1 else root / "src/Peakle/bin/Release/Peakle.dll"
out = root / "publish"
author = "SlopGaming"

GITHUB = {
    "README.md": "README.md",
    "LICENSE": "LICENSE",
    "src/Peakle/Peakle.csproj": "src/Peakle/Peakle.csproj",
    "src/Peakle/Resources/logo.png": "src/Peakle/Resources/logo.png",
    "package/manifest.json": "package/manifest.json",
    "package/README.md": "package/README.md",
    "package/CHANGELOG.md": "package/CHANGELOG.md",
    "package/icon.png": "package/icon.png",
    "tools/deploy_local_mod.py": "tools/deploy_local_mod.py",
    "tools/pack_release.py": "tools/pack_release.py",
    "tools/make_publish.py": "tools/make_publish.py",
    "icon.png": "art/icon.png",
    "logo.png": "art/logo.png",
}
GITIGNORE = """bin/
obj/
*.user.props
dist/
publish/
_decomp/
"""
THUNDERSTORE = ["manifest.json", "README.md", "CHANGELOG.md", "icon.png"]

# Start clean, but keep the GitHub folder's git repository (publish/github/.git).
if out.exists():
    for entry in out.iterdir():
        if entry.name == "github" and entry.is_dir():
            for sub in entry.iterdir():
                if sub.name != ".git":
                    shutil.rmtree(sub) if sub.is_dir() else sub.unlink()
        else:
            shutil.rmtree(entry) if entry.is_dir() else entry.unlink()

# GitHub
gh = out / "github"
gh.mkdir(parents=True, exist_ok=True)
files = dict(GITHUB)
for cs in sorted((root / "src/Peakle").glob("*.cs")):
    files[f"src/Peakle/{cs.name}"] = f"src/Peakle/{cs.name}"
for src, dst in files.items():
    target = gh / dst
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(root / src, target)
(gh / ".gitignore").write_text(GITIGNORE, encoding="utf8")
(gh / "docs/screenshots").mkdir(parents=True)
for shot in sorted((root / "docs/screenshots").glob("*.jpg")) + sorted((root / "docs/screenshots").glob("*.png")):
    shutil.copy2(shot, gh / "docs/screenshots" / shot.name)

# Thunderstore
ts = out / "thunderstore"
ts.mkdir(parents=True)
for f in THUNDERSTORE:
    shutil.copy2(root / "package" / f, ts / f)
shutil.copy2(dll, ts / dll.name)
manifest = json.loads((ts / "manifest.json").read_text(encoding="utf8"))
zip_path = out / f"{author}-{manifest['name']}-{manifest['version_number']}.zip"
with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
    for f in sorted(ts.iterdir()):
        z.write(f, f.name)

print(f"github:       {gh} ({sum(1 for p in gh.rglob('*') if p.is_file() and '.git' not in p.relative_to(gh).parts[:1])} files)")
print(f"thunderstore: {ts}")
print(f"zip:          {zip_path}")
