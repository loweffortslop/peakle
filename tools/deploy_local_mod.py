"""Install the freshly built Peakle into a Thunderstore Mod Manager profile as a local mod.

Does what the manager's "Import local mod" does, so Peakle shows up in the profile's mod list with its name,
icon, description and version:
  - the package (DLL, icon.png, manifest.json, README.md) goes to <DataFolder>/<game>/cache/<Author-Name>/<version>/
    and to <profile>/BepInEx/plugins/<Author-Name>/
  - the profile's mods.yml gets (or updates) an entry for it, marked as a local (not online) mod.
The manager reads these when it starts, so restart it after a build to see changes.

usage: deploy_local_mod.py <profile dir> <built dll> <package dir> <Plugin.cs> <author>
"""
import json
import re
import shutil
import sys
import time
from pathlib import Path

profile, dll, package, plugin_cs = (Path(a) for a in sys.argv[1:5])
author = sys.argv[5]

source = plugin_cs.read_text(encoding="utf8")
version = re.search(r'Version\s*=\s*"(\d+)\.(\d+)\.(\d+)"', source)
if not version:
    sys.exit("deploy_local_mod: no Version in Plugin.cs")
major, minor, patch = (int(v) for v in version.groups())
version_text = f"{major}.{minor}.{patch}"

manifest = json.loads((package / "manifest.json").read_text(encoding="utf8"))
manifest["version_number"] = version_text
(package / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf8")
name = manifest["name"]
full_name = f"{author}-{name}"


def fill(folder: Path):
    if folder.exists():
        shutil.rmtree(folder)
    folder.mkdir(parents=True)
    shutil.copy2(dll, folder / dll.name)
    for f in ("icon.png", "manifest.json", "README.md", "CHANGELOG.md"):
        if (package / f).exists():
            shutil.copy2(package / f, folder / f)


game_data = profile.parent.parent  # .../DataFolder/<game>
fill(game_data / "cache" / full_name / version_text)
plugins = profile / "BepInEx" / "plugins"
fill(plugins / full_name)
# Earlier installs under another folder name (plain dev deploy, or another author) must not load twice.
for old in [plugins / name, *plugins.glob(f"*-{name}")]:
    if old.exists() and old.name != full_name:
        shutil.rmtree(old)
for old in (game_data / "cache").glob(f"*-{name}"):
    if old.name != full_name:
        shutil.rmtree(old)
# Only the version just built stays in the cache.
for old in (game_data / "cache" / full_name).iterdir():
    if old.is_dir() and old.name != version_text:
        shutil.rmtree(old)

# mods.yml: replace our entry (or append it). Plain text, matching the manager's own formatting.
description = manifest.get("description", "").replace("'", "''")
deps = manifest.get("dependencies", [])
entry = (
    "- manifestVersion: 1\n"
    f"  name: {full_name}\n"
    f"  authorName: {author}\n"
    f"  websiteUrl: '{manifest.get('website_url', '')}'\n"
    f"  displayName: {name}\n"
    f"  description: '{description}'\n"
    "  gameVersion: '0'\n"
    "  networkMode: both\n"
    "  packageType: other\n"
    "  installMode: managed\n"
    f"  installedAtTime: {int(time.time() * 1000)}\n"
    "  loaders: []\n"
    + ("  dependencies:\n" + "".join(f"    - {d}\n" for d in deps) if deps else "  dependencies: []\n")
    + "  incompatibilities: []\n"
    "  optionalDependencies: []\n"
    "  versionNumber:\n"
    f"    major: {major}\n"
    f"    minor: {minor}\n"
    f"    patch: {patch}\n"
    "  enabled: true\n"
    "  onlineSource: false\n"
    "  trustedPackage: false\n"
)
mods = profile / "mods.yml"
text = mods.read_text(encoding="utf8") if mods.exists() else ""
blocks = re.split(r"(?m)^(?=- manifestVersion:)", text)
ours = re.compile(rf"(?m)^  name: \S*-{re.escape(name)}\s*$")  # this mod, under any author
kept = [b for b in blocks if b.strip() and not ours.search(b)]
was_enabled = all(not ours.search(b) or "enabled: true" in b for b in blocks)
if not was_enabled:
    entry = entry.replace("  enabled: true\n", "  enabled: false\n")  # keep the user's on/off choice
kept = [b if b.endswith("\n") else b + "\n" for b in kept]
mods.write_text("".join(kept) + entry, encoding="utf8")
if not was_enabled:
    # The manager disables a mod by renaming its DLLs to .old.
    deployed = plugins / full_name / dll.name
    deployed.replace(deployed.with_name(dll.name + ".old"))
print(f"deploy_local_mod: {full_name} {version_text} installed in '{profile.name}'")
