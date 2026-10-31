// Cuts a square of real elevation around a plot out of the Copernicus GLO-30 DEM (30 m), for
// Tools/blender/make_terrain.py. Usage: node Tools/dem_extract.mjs <plotX> <plotY>
// Writes RawAssets/processed/dem_<x>_<y>.bin: float32 heights in metres, row by row from north-west,
// SIZE x SIZE samples STEP metres apart, centred on the plot; rows run south (+Y in the game).
// Needs the DEM tiles in RawAssets/dem (Tools/setup_assets.ps1 downloads them) and the geotiff package.
import { fromFile } from "../RawAssets/node_modules/geotiff/dist-node/geotiff.js";
import { writeFile, access } from "node:fs/promises";
import { join } from "node:path";

const [plotX, plotY] = process.argv.slice(2).map(Number);
const STEP = 30;
const HALF = 9000;
const SIZE = (2 * HALF) / STEP + 1;

// The game's world grid (5 km plots) to latitude/longitude: the projection of the 2D client's world_data.gd
const WIDTH = 6933, HEIGHT = 2942, PLOT_KM = 5, RADIUS_KM = 6371.0072, COS30 = Math.cos(Math.PI / 6);
const lon0 = (((plotX + 0.5) * PLOT_KM - (WIDTH * PLOT_KM) / 2) / (RADIUS_KM * COS30)) * (180 / Math.PI);
const lat0 = Math.asin(Math.max(-1, Math.min(1, (((HEIGHT * PLOT_KM) / 2 - (plotY + 0.5) * PLOT_KM) * COS30) / RADIUS_KM))) * (180 / Math.PI);

const tiles = new Map();
async function tile(lat, lon) {
  const ns = lat >= 0 ? "N" : "S", ew = lon >= 0 ? "E" : "W";
  const name = `Copernicus_DSM_COG_10_${ns}${String(Math.abs(lat)).padStart(2, "0")}_00_${ew}${String(Math.abs(lon)).padStart(3, "0")}_00_DEM`;
  if (!tiles.has(name)) {
    const path = join("RawAssets", "dem", `${name}.tif`);
    try { await access(path); } catch { tiles.set(name, null); return null; }
    const image = await (await fromFile(path)).getImage();
    const [raster] = await image.readRasters();
    tiles.set(name, { raster, width: image.getWidth(), height: image.getHeight() });
  }
  return tiles.get(name);
}

async function heightAt(lat, lon) {
  const t = await tile(Math.floor(lat), Math.floor(lon));
  if (!t) return 0;
  // A tile spans one degree; pixel centres, north row first
  const fx = (lon - Math.floor(lon)) * (t.width - 1);
  const fy = (Math.floor(lat) + 1 - lat) * (t.height - 1);
  const x0 = Math.floor(fx), y0 = Math.floor(fy);
  const x1 = Math.min(x0 + 1, t.width - 1), y1 = Math.min(y0 + 1, t.height - 1);
  const ax = fx - x0, ay = fy - y0;
  const v = (x, y) => t.raster[y * t.width + x];
  return (v(x0, y0) * (1 - ax) + v(x1, y0) * ax) * (1 - ay) + (v(x0, y1) * (1 - ax) + v(x1, y1) * ax) * ay;
}

const metresPerLat = 111320;
const metresPerLon = 111320 * Math.cos((lat0 * Math.PI) / 180);
const out = new Float32Array(SIZE * SIZE);
for (let row = 0; row < SIZE; row++) {
  const south = -HALF + row * STEP;
  for (let col = 0; col < SIZE; col++) {
    const east = -HALF + col * STEP;
    out[row * SIZE + col] = await heightAt(lat0 - south / metresPerLat, lon0 + east / metresPerLon);
  }
}
const file = join("RawAssets", "processed", `dem_${plotX}_${plotY}.bin`);
await writeFile(file, Buffer.from(out.buffer));
console.log(`FWDEM ${file}: ${SIZE}x${SIZE} at ${STEP} m around ${lat0.toFixed(4)}N ${lon0.toFixed(4)}E, ` +
  `${out.reduce((a, b) => Math.min(a, b)).toFixed(0)}..${out.reduce((a, b) => Math.max(a, b)).toFixed(0)} m`);
