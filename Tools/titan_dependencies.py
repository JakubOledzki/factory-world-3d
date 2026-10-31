# Run inside Project Titan (read-only use): lists the conifer meshes we want and every package they
# depend on (materials, textures, functions), written to the file given as FW_OUT.
#   UnrealEditor-Cmd.exe D:\Titan\ProjectTitan\Titan.uproject -run=pythonscript -script="...\titan_dependencies.py"
import os

import unreal

OUT = os.environ.get("FW_OUT", "titan_dependencies.txt")
ROOTS = [
    "/Game/Environment/Foliage/Trees/SM_Pine_A",
    "/Game/Environment/Foliage/Trees/SM_Pine_B",
    "/Game/Environment/Foliage/Trees/SM_Pine_A_Lichen",
    "/Game/Environment/Foliage/Trees/SM_Pine_B_Lichen",
    "/Game/Environment/Foliage/Trees/SM_Sapling_A",
    "/Game/Environment/Foliage/Trees/SM_Sapling_B",
]
registry = unreal.AssetRegistryHelpers.get_asset_registry()
registry.search_all_assets(True)
options = unreal.AssetRegistryDependencyOptions(include_soft_package_references=False, include_hard_package_references=True,
                                                include_searchable_names=False, include_soft_management_references=False,
                                                include_hard_management_references=False)
seen = set()
stack = list(ROOTS)
while stack:
    package = stack.pop()
    if package in seen or not package.startswith("/Game/"):
        continue
    seen.add(package)
    for dependency in registry.get_dependencies(package, options) or []:
        stack.append(str(dependency))
for root in ROOTS:
    data = registry.get_assets_by_package_name(root)
    unreal.log(f"FWTITAN root {root}: {'found' if data else 'MISSING'}")
with open(OUT, "w", encoding="utf-8") as f:
    for package in sorted(seen):
        f.write(package + "\n")
unreal.log(f"FWTITAN {len(seen)} packages -> {OUT}")
