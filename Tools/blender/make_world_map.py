# Paints the world map's pictures from the 2D game's world (RawAssets/world, from Tools/build_world_map.mjs) and the
# shaded relief of Natural Earth (SR_HR, public domain: RawAssets/naturalearth/SR_HR.tif, 21600 x 10800, from
# naciscdn.org/naturalearth/10m/raster/SR_HR.zip), in the game's projection (Behrmann, plots of 5 x 5 km):
#   RawAssets/processed/worldmap/world_overview.png   the whole Earth, 8192 x 4096 (with mipmaps, seen from afar)
#   RawAssets/processed/worldmap/world_detail_<r>_<c>.png  3 x 2 pieces at 3 pixels a plot, for closer up
#   RawAssets/processed/worldmap/world_clouds.png    a tileable layer of clouds (alpha), drifting over the map
#   RawAssets/processed/worldmap/world_biomes.png    each plot's biome as a grey level (biome * 16 + 8), for the map's
#                                                    material close up (M_FWWorldMap)
# Land is coloured by biome as seen from orbit (forest, fields, steppe, desert, rock, tundra, ice, towns), blended
# softly between plots and shaded by the relief; the sea grows shallow and turquoise towards the coast; country
# borders are faint lines. Run with Blender's Python (for numpy):
#   blender --background --factory-startup --python Tools/blender/make_world_map.py
import gzip
import math
import os

import bpy
import numpy as np

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "RawAssets")
OUT = os.path.join(ROOT, "processed", "worldmap")
RADIUS, PLOT = 6371.0072, 5.0
COS = math.cos(math.radians(30))
SCALE = 3  # pixels a plot in the detail pieces
PIECES = (2, 3)  # rows, columns of detail pieces

# Biome colours from orbit (sRGB 0-1): ocean, lake, forest, farmland, grassland, desert, mountains, tundra, ice,
# city, coast, lakeshore, oasis
COLORS = np.array([
    (0.03, 0.09, 0.17), (0.06, 0.17, 0.25), (0.11, 0.21, 0.09), (0.37, 0.41, 0.19), (0.45, 0.46, 0.24),
    (0.80, 0.66, 0.44), (0.47, 0.42, 0.36), (0.47, 0.45, 0.36), (0.93, 0.96, 0.99), (0.52, 0.50, 0.48),
    (0.55, 0.54, 0.38), (0.38, 0.44, 0.24), (0.36, 0.47, 0.21),
], dtype=np.float32)
SHALLOW = np.array((0.07, 0.33, 0.40), dtype=np.float32)


def load_world():
    biomes = gzip.decompress(open(os.path.join(ROOT, "world", "biomes.bin.gz"), "rb").read())
    assert biomes[:4] == b"FWW1"
    width = int.from_bytes(biomes[4:6], "little")
    height = int.from_bytes(biomes[6:8], "little")
    grid = np.frombuffer(biomes, dtype=np.uint8, offset=8, count=width * height).reshape(height, width)
    raw = gzip.decompress(open(os.path.join(ROOT, "world", "countries.bin.gz"), "rb").read())
    at = 10
    for _ in range(int.from_bytes(raw[8:10], "little")):
        at = raw.index(b"\n", at) + 1
    countries = np.frombuffer(raw, dtype=np.uint8, offset=at, count=width * height).reshape(height, width)
    return np.minimum(grid, 12), countries


def load_relief():
    """SR_HR: uncompressed 8-bit, one row a strip, the rows one after another."""
    path = os.path.join(ROOT, "naturalearth", "SR_HR.tif")
    data = np.memmap(path, dtype=np.uint8, mode="r")
    # The first strip's offset from the TIFF's StripOffsets
    first = int(np.frombuffer(data[233342366:233342370].tobytes(), dtype="<u4")[0])
    return np.asarray(data[first:first + 21600 * 10800]).reshape(10800, 21600)


def bilinear(grid, xs, ys, wrap=True):
    """Samples a 2D (or 2D x channels) grid at fractional positions; columns wrap east-west."""
    h, w = grid.shape[:2]
    x0 = np.floor(xs).astype(np.int64)
    y0 = np.clip(np.floor(ys).astype(np.int64), 0, h - 1)
    fx = (xs - x0).astype(np.float32)
    fy = (ys - np.floor(ys)).astype(np.float32)
    y1 = np.clip(y0 + 1, 0, h - 1)
    x1 = x0 + 1
    if wrap:
        x0 %= w
        x1 %= w
    else:
        x0 = np.clip(x0, 0, w - 1)
        x1 = np.clip(x1, 0, w - 1)
    if grid.ndim == 3:
        fx = fx[..., None]
        fy = fy[..., None]
    a = grid[y0, x0] * (1 - fx) + grid[y0, x1] * fx
    b = grid[y1, x0] * (1 - fx) + grid[y1, x1] * fx
    return a * (1 - fy) + b * fy


def value_noise(shape_y, shape_x, cells, seed):
    rng = np.random.default_rng(seed)
    grid = rng.random((cells[0] + 2, cells[1] + 1), dtype=np.float32)
    ys = np.linspace(0, cells[0], shape_y, endpoint=False, dtype=np.float32)
    xs = np.linspace(0, cells[1], shape_x, endpoint=False, dtype=np.float32)
    return bilinear(grid, xs[None, :].repeat(shape_y, 0), ys[:, None].repeat(shape_x, 1))


def save_png(name, pixels):
    """pixels: H x W x 3 or 4, top row first, sRGB 0-1."""
    h, w = pixels.shape[:2]
    if pixels.shape[2] == 3:
        pixels = np.concatenate([pixels, np.ones((h, w, 1), np.float32)], axis=2)
    image = bpy.data.images.new(name, w, h, alpha=True, float_buffer=False)
    image.colorspace_settings.name = "sRGB"
    image.pixels.foreach_set(np.ascontiguousarray(pixels[::-1], dtype=np.float32).ravel())
    image.filepath_raw = os.path.join(OUT, name + ".png")
    image.file_format = "PNG"
    image.save()
    bpy.data.images.remove(image)
    print(f"FWMAP {name} {w} x {h}")


def plot_colors(biomes, countries):
    """Each plot's colour (H x W x 3), the sea shading from deep to shallow towards land."""
    colors = COLORS[biomes].copy()
    water = (biomes == 0)
    # Distance to land in plots, up to 24: shallow seas along coasts
    distance = np.full(biomes.shape, 24, np.float32)
    front = ~water
    reach = front.copy()
    for d in range(1, 24):
        grown = reach | np.roll(reach, 1, 1) | np.roll(reach, -1, 1)
        grown[1:] |= reach[:-1]
        grown[:-1] |= reach[1:]
        newly = grown & ~reach
        distance[newly] = d
        reach = grown
    shallow = np.clip(1.0 - (distance - 1) / 14.0, 0, 1) ** 1.6
    colors[water] = colors[water] * (1 - shallow[water, None]) + SHALLOW * shallow[water, None]
    # Farmland and steppe vary from plot to plot, as fields and grass do
    rng = np.random.default_rng(3)
    vary = (rng.random(biomes.shape, dtype=np.float32) - 0.5) * 0.10
    land = ~water & (biomes != 1) & (biomes != 8)
    colors[land] *= (1 + vary[land, None])
    return colors


def paint(colors, biomes, countries, relief, y0, y1, x0, x1, scale):
    """The map from pixel rows y0..y1, columns x0..x1 at `scale` pixels a plot."""
    h, w = biomes.shape
    py = (np.arange(y0, y1, dtype=np.float32) + 0.5) / scale
    px = (np.arange(x0, x1, dtype=np.float32) + 0.5) / scale
    PX, PY = np.meshgrid(px, py)
    # Soft blend of the plots' colours (plot centres at +0.5)
    rgb = bilinear(colors, PX - 0.5, PY - 0.5)
    # Longitude and latitude, and the shaded relief there
    lon = np.degrees((PX * PLOT - w * PLOT / 2) / (RADIUS * COS))
    lat = np.degrees(np.arcsin(np.clip((h * PLOT / 2 - PY * PLOT) * COS / RADIUS, -1, 1)))
    sx = (lon + 180) / 360 * 21600 - 0.5
    sy = (90 - lat) / 180 * 10800 - 0.5
    shade = bilinear(relief, sx, sy).astype(np.float32) / 200.0
    plot_x = np.floor(PX).astype(np.int64) % w
    plot_y = np.clip(np.floor(PY).astype(np.int64), 0, h - 1)
    here = biomes[plot_y, plot_x]
    water = (here == 0) | (here == 1)
    # Relief shows strongly on rock and ice, softly elsewhere, not at all on water
    strength = np.where(water, 0.0, np.where((here == 6) | (here == 8) | (here == 7), 1.15, 0.8)).astype(np.float32)
    shade = 1 + (np.clip(shade, 0.35, 1.35) - 1) * strength
    rgb = rgb * shade[..., None]
    # Snow on the bright high ridges of mountains facing the light, as from orbit
    snow = ((here == 6) & (shade > 1.08)) * np.clip((np.abs(lat) - 25) / 30, 0.15, 1.0)
    rgb = rgb * (1 - snow[..., None] * 0.6) + np.float32(0.9) * snow[..., None] * 0.6
    # Fine grain: forests' canopy, fields, sand
    grain = value_noise(y1 - y0, x1 - x0, ((y1 - y0) // 3, (x1 - x0) // 3), 11 + y0 * 7 + x0)
    grain2 = value_noise(y1 - y0, x1 - x0, ((y1 - y0) // 17, (x1 - x0) // 17), 13 + y0 * 3 + x0)
    rough = np.where(water, 0.03, 0.12).astype(np.float32)
    rgb = rgb * (1 + (grain[..., None] - 0.5) * rough[..., None] + (grain2[..., None] - 0.5) * rough[..., None])
    # Country borders: where the plot east or south lies in another country, at that edge
    edge_x = (np.floor(PX + 1.0 / scale) != np.floor(PX))
    edge_y = (np.floor(PY + 1.0 / scale) != np.floor(PY))
    country = countries[plot_y, plot_x]
    east = countries[plot_y, (plot_x + 1) % w]
    south = countries[np.clip(plot_y + 1, 0, h - 1), plot_x]
    border = ((edge_x & (east != country)) | (edge_y & (south != country))) & (country > 0)
    rgb[border] = rgb[border] * 0.45 + np.float32(0.95) * 0.25
    return np.clip(rgb, 0, 1)


def clouds(size=(1024, 2048)):
    """Tileable clouds: periodic fractal noise from filtered random phases, white with alpha."""
    rng = np.random.default_rng(21)
    h, w = size
    ky = np.fft.fftfreq(h)[:, None]
    kx = np.fft.fftfreq(w)[None, :]
    k = np.sqrt(kx * kx + ky * ky)
    k[0, 0] = 1
    spectrum = (rng.normal(size=(h, w)) + 1j * rng.normal(size=(h, w))) / k ** 1.6
    field = np.real(np.fft.ifft2(spectrum)).astype(np.float32)
    field = (field - field.mean()) / field.std()
    alpha = np.clip((field - 0.25) / 1.4, 0, 1) ** 1.3
    shade = np.clip(1.0 - 0.25 * np.clip(field - 0.9, 0, 2), 0.6, 1.0)
    rgba = np.stack([shade, shade, shade * 1.02, alpha * 0.95], axis=2)
    return np.clip(rgba, 0, 1).astype(np.float32)


os.makedirs(OUT, exist_ok=True)
biomes, countries = load_world()
grey = (biomes.astype(np.float32) * 16 + 8) / 255.0
save_png("world_biomes", np.repeat(grey[..., None], 3, axis=2))
if os.environ.get("FW_ONLY") == "biomes":
    raise SystemExit
relief = load_relief()
h, w = biomes.shape
colors = plot_colors(biomes, countries)

# The overview: 8192 x 4096 over the whole map (stretched a little up and down; the game draws it at the map's size)
OW, OH = 8192, 4096
over = np.zeros((OH, OW, 3), np.float32)
band = 512
for y in range(0, OH, band):
    sy, sx = OH / h, OW / w
    # paint() works at an even scale: paint at the overview's x scale and resample rows
    rows = paint(colors, biomes, countries, relief, int(y / sy * sx), int((y + band) / sy * sx) + 1, 0, OW, sx)
    pick = np.clip(((np.arange(y, y + band) + 0.5) / sy * sx - int(y / sy * sx)).astype(np.int64), 0, rows.shape[0] - 1)
    over[y:y + band] = rows[pick]
save_png("world_overview", over)
del over

# The detail pieces at SCALE pixels a plot
TH, TW = h * SCALE, w * SCALE
rows_at = np.linspace(0, TH, PIECES[0] + 1).astype(int)
cols_at = np.linspace(0, TW, PIECES[1] + 1).astype(int)
for r in range(PIECES[0]):
    for c in range(PIECES[1]):
        save_png(f"world_detail_{r}_{c}", paint(colors, biomes, countries, relief, rows_at[r], rows_at[r + 1], cols_at[c], cols_at[c + 1], SCALE))

save_png("world_clouds", clouds())
print("FWMAP done")
