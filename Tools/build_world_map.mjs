// Builds the world map for the 3D client from the 2D game's world files (github.com/JakubOledzki/factory-world,
// client/world: the biome of every 5 × 5 km plot, the countries and the towns, made by its tools/build_world.py):
//   RawAssets/world/*.gz            the files, downloaded once at a fixed commit
//   RawAssets/processed/world_map.png   the map picture: plots coloured by biome as in the 2D map, country borders
// The game reads the .gz files itself (Content/External/WorldData, copied there by this script) for the plot under
// the cursor and the town names. Run: node Tools/build_world_map.mjs
import { mkdir, writeFile, readFile, access, copyFile } from "node:fs/promises";
import { gunzipSync, deflateSync } from "node:zlib";
import { join } from "node:path";

const COMMIT = "43eb0d9";
const SOURCE = `https://raw.githubusercontent.com/JakubOledzki/factory-world/${COMMIT}/client/world`;
const FILES = ["biomes.bin.gz", "countries.bin.gz", "towns.tsv.gz"];
const RAW = join(process.cwd(), "RawAssets", "world");
const OUT = join(process.cwd(), "RawAssets", "processed", "world_map.png");
const STAGED = join(process.cwd(), "Content", "External", "WorldData");

// Biome colours as the 2D map draws them (world_data.gd COLORS), by biome id
const COLORS = ["#0b1620", "#1d4257", "#1f4a2b", "#6f8a35", "#a5a052", "#d6ad52", "#75624f", "#7f8f86", "#d9e3e8",
  "#a39d9a", "#3d8a84", "#4f8fa8", "#5f9a4a"].map((h) => [1, 3, 5].map((i) => parseInt(h.slice(i, i + 2), 16)));

const exists = (p) => access(p).then(() => true, () => false);

await mkdir(RAW, { recursive: true });
await mkdir(STAGED, { recursive: true });
for (const file of FILES) {
  const path = join(RAW, file);
  if (!(await exists(path))) {
    const response = await fetch(`${SOURCE}/${file}`);
    if (!response.ok) throw new Error(`${response.status} ${file}`);
    await writeFile(path, Buffer.from(await response.arrayBuffer()));
  }
  await copyFile(path, join(STAGED, file));
}

const biomes = gunzipSync(await readFile(join(RAW, "biomes.bin.gz")));
if (biomes.toString("ascii", 0, 4) !== "FWW1") throw new Error("biomes file broken");
const width = biomes.readUInt16LE(4), height = biomes.readUInt16LE(6);
const countriesRaw = gunzipSync(await readFile(join(RAW, "countries.bin.gz")));
let at = 10;
for (let i = 0; i < countriesRaw.readUInt16LE(8); i++) at = countriesRaw.indexOf(10, at) + 1;
const countries = countriesRaw.subarray(at);

// One row per scanline (filter byte 0), RGB
const rows = Buffer.alloc((width * 3 + 1) * height);
for (let y = 0; y < height; y++) {
  const row = y * (width * 3 + 1);
  for (let x = 0; x < width; x++) {
    const i = y * width + x;
    let [r, g, b] = COLORS[Math.min(12, biomes[8 + i])];
    // A little grain, as the 2D map's shader adds
    let h = Math.imul(x, 374761393) + Math.imul(y, 668265263) >>> 0;
    h = Math.imul(h ^ (h >>> 13), 1274126177) >>> 0;
    const grain = 0.94 + 0.09 * ((h ^ (h >>> 16)) % 1000) / 1000;
    r *= grain; g *= grain; b *= grain;
    // Country borders: a plot whose neighbour east or south lies in another country is darkened
    const c = countries[i];
    if (c && ((x + 1 < width && countries[i + 1] !== c) || (y + 1 < height && countries[i + width] !== c))) {
      r *= 0.5; g *= 0.5; b *= 0.5;
    }
    rows[row + 1 + x * 3] = Math.min(255, r);
    rows[row + 2 + x * 3] = Math.min(255, g);
    rows[row + 3 + x * 3] = Math.min(255, b);
  }
}

// A minimal PNG writer
const crcTable = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});
const crc = (buf) => { let c = 0xffffffff; for (const b of buf) c = crcTable[(c ^ b) & 255] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; };
const chunk = (type, data) => {
  const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type), data]);
  const sum = Buffer.alloc(4); sum.writeUInt32BE(crc(body));
  return Buffer.concat([len, body, sum]);
};
const header = Buffer.alloc(13);
header.writeUInt32BE(width, 0); header.writeUInt32BE(height, 4);
header[8] = 8; header[9] = 2; // 8-bit RGB
await mkdir(join(process.cwd(), "RawAssets", "processed"), { recursive: true });
await writeFile(OUT, Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk("IHDR", header),
  chunk("IDAT", deflateSync(rows, { level: 6 })), chunk("IEND", Buffer.alloc(0))]));
console.log(`world map ${width} x ${height} -> ${OUT}`);
