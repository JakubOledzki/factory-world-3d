// Downloads the CC0 Poly Haven assets the game uses into RawAssets/polyhaven (not in git; the
// imported .uasset files are). Run: node Tools/fetch_polyhaven.mjs
// Every asset here is listed in ASSETS.md with its URL and licence (CC0).
import { mkdir, writeFile, access, readFile } from "node:fs/promises";
import { createHash } from "node:crypto";
import { dirname, join } from "node:path";

const OUT = join(process.cwd(), "RawAssets", "polyhaven");

// Models: glTF with 2K textures
const MODELS = [
  "boulder_01", "namaqualand_boulder_02", "namaqualand_boulder_04", "rock_moss_set_01", "rock_09",
  "fir_tree_01", "pine_tree_01", "fir_sapling_medium", "shrub_01", "shrub_02", "fern_02", "grass_medium_01",
];

// Ground and cliff textures: [id, resolution]
const TEXTURES = [
  ["rocky_terrain_02", "4k"], ["aerial_grass_rock", "4k"], ["gravel_ground_01", "2k"], ["snow_02", "2k"],
  ["forest_ground_04", "2k"], ["coast_sand_01", "2k"], ["rock_face_03", "4k"], ["dirt", "2k"],
  // Factory materials (Tools/blender/make_factory.py names its material slots after these)
  ["metal_plate", "2k"], ["blue_metal_plate", "2k"], ["rusty_painted_metal", "2k"], ["rubberized_track", "2k"],
  ["weathered_planks", "2k"], ["concrete_floor_worn_001", "2k"], ["metal_grate_rusty", "2k"],
  // Workshop furniture and kilns
  ["red_brick_03", "2k"], ["castle_wall_slates", "2k"], ["wood_table_001", "2k"],
  // Character clothes (Tools/blender/make_character_gear.py), tinted with the server's colours
  ["cotton_jersey", "2k"], ["knitted_fleece", "2k"], ["denim_fabric_05", "2k"], ["leather_white", "2k"],
];
const MAPS = { Diffuse: "diff", nor_gl: "nor_gl", arm: "arm" };

async function exists(path) {
  try { await access(path); return true; } catch { return false; }
}

const md5 = (data) => createHash("md5").update(data).digest("hex");

// Downloads unless already there with the right checksum (Poly Haven lists an md5 for every file)
async function download(url, path, sum) {
  if (await exists(path) && (!sum || md5(await readFile(path)) === sum)) return;
  await mkdir(dirname(path), { recursive: true });
  const response = await fetch(url);
  if (!response.ok) throw new Error(`${response.status} ${url}`);
  const data = Buffer.from(await response.arrayBuffer());
  if (sum && md5(data) !== sum) throw new Error(`checksum mismatch: ${url}`);
  await writeFile(path, data);
  console.log("  " + path.slice(OUT.length + 1));
}

async function files(id) {
  const response = await fetch(`https://api.polyhaven.com/files/${id}`);
  if (!response.ok) throw new Error(`${response.status} files/${id}`);
  return response.json();
}

for (const id of MODELS) {
  console.log(id);
  const all = await files(id);
  const gltf = all.gltf["2k"].gltf;
  // Twig cards need their alpha map, which the glTF package leaves out (prepare_polyhaven.py merges it)
  const alpha = all.twig_alpha?.["2k"]?.png ?? all.twig_alpha?.["2k"]?.jpg;
  if (alpha) {
    const ext = alpha.url.endsWith(".png") ? "png" : "jpg";
    await download(alpha.url, join(OUT, "models", id, "textures", `${id}_twig_alpha_2k.${ext}`), alpha.md5);
  }
  await download(gltf.url, join(OUT, "models", id, `${id}.gltf`), gltf.md5);
  for (const [relative, file] of Object.entries(gltf.include)) {
    await download(file.url, join(OUT, "models", id, relative), file.md5);
  }
}

for (const [id, resolution] of TEXTURES) {
  console.log(id);
  const all = await files(id);
  for (const [key, suffix] of Object.entries(MAPS)) {
    const map = all[key]?.[resolution]?.jpg ?? all[key]?.[resolution]?.png;
    if (!map) { console.log(`  (no ${key})`); continue; }
    const ext = map.url.endsWith(".png") ? "png" : "jpg";
    await download(map.url, join(OUT, "textures", id, `${id}_${suffix}_${resolution}.${ext}`), map.md5);
  }
}
console.log("done");
