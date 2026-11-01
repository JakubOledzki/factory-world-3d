# Footsteps and landings from recordings (CC0, OpenGameArt; downloaded by Tools/setup_assets.ps1 to
# RawAssets/footsteps, see ASSETS.md): one step per file, trimmed to its contact, faded out, levelled per surface
# and written as RawAssets/processed/audio/step_<surface>_<n>.wav and land_<surface>.wav for
# Tools/import_external.py. Run in Blender (its audio library reads FLAC): blender --background --python this
import os
import struct

import aud
import numpy as np

ROOT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "RawAssets")
SOURCE = os.path.join(ROOT, "footsteps")
OUT = os.path.join(ROOT, "processed", "audio")
RATE = 48000
VARIANTS = 6

FANTOZZI = os.path.join(SOURCE, "Fantozzi-footsteps", "Fantozzi-footsteps", "flac")
SNOW = os.path.join(SOURCE, "corsica_s-walking_in_snow", "Corsica_S-Walking_in_Snow")
METAL = os.path.join(SOURCE, "metal_steps_48k24b")
# Surface: its recordings and loudness (peak) against the others
SURFACES = {
    "grass": ([os.path.join(FANTOZZI, f"Fantozzi-Sand{side}{i}.flac") for side in "LR" for i in (1, 2, 3)], 0.7),
    "rock": ([os.path.join(FANTOZZI, f"Fantozzi-Stone{side}{i}.flac") for side in "LR" for i in (1, 2, 3)], 0.75),
    "snow": ([os.path.join(SNOW, f"Corsica_S-Walking_on_snow_covered_gravel_{i:02d}.flac") for i in (2, 3, 5, 6, 8, 9)], 0.7),
    "metal": ([os.path.join(METAL, f"metal_steps_{i:02d}.wav") for i in (1, 3, 5, 6, 9, 11)], 0.6),
}


def load(path):
    """Mono at RATE."""
    sound = aud.Sound(path).resample(RATE, False)
    data = sound.data()
    return data.mean(axis=1) if data.ndim > 1 else data


def step(samples, peak):
    """From just before the contact to where it has died away, with a short fade in and out."""
    level = np.convolve(np.abs(samples), np.ones(240) / 240, "same")
    loud = np.where(level > level.max() * 0.08)[0]
    start = max(0, loud[0] - int(0.008 * RATE))
    end = min(len(samples), loud[-1] + int(0.06 * RATE), start + int(0.6 * RATE))
    out = samples[start:end].copy()
    fade_in, fade_out = int(0.004 * RATE), int(0.04 * RATE)
    out[:fade_in] *= np.linspace(0, 1, fade_in)
    out[-fade_out:] *= np.linspace(1, 0, fade_out)
    return out / np.abs(out).max() * peak


def lowpass(samples, cutoff):
    spectrum = np.fft.rfft(samples)
    spectrum[np.fft.rfftfreq(len(samples), 1 / RATE) > cutoff] = 0
    return np.fft.irfft(spectrum, len(samples))


def land(first, second, peak):
    """Both feet coming down just apart, with the weight in the low end."""
    gap = int(0.03 * RATE)
    out = np.zeros(max(len(first), len(second) + gap))
    out[:len(first)] += first
    out[gap:gap + len(second)] += second * 0.8
    out += lowpass(out, 250) * 1.5
    return out / np.abs(out).max() * peak


def save(name, samples):
    pcm = (np.clip(samples, -1, 1) * 32767).astype("<i2").tobytes()
    with open(os.path.join(OUT, f"{name}.wav"), "wb") as f:
        f.write(b"RIFF" + struct.pack("<I", 36 + len(pcm)) + b"WAVEfmt " + struct.pack("<IHHIIHH", 16, 1, 1, RATE, RATE * 2, 2, 16))
        f.write(b"data" + struct.pack("<I", len(pcm)) + pcm)


os.makedirs(OUT, exist_ok=True)
for surface, (files, peak) in SURFACES.items():
    steps = [step(load(path), peak) for path in files[:VARIANTS]]
    for i, samples in enumerate(steps):
        save(f"step_{surface}_{i + 1}", samples)
    save(f"land_{surface}", land(steps[0], steps[1], min(1.0, peak * 1.25)))
print("FW footsteps done")
