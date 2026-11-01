# Synthesizes the game's sounds (no recordings, so no licences): ambience, jumps, factory machines and UI; the
# footsteps are recordings (Tools/make_footsteps.py). Written as 48 kHz WAV files in RawAssets/processed/audio
# (Tools/import_external.py imports them). Runs in Blender's Python, which has numpy:
#   blender --background --factory-startup --python Tools/make_audio.py
# Loops are made seamless by crossfading their end into their start. Deterministic (fixed seeds).
import os
import wave

import numpy as np

RATE = 48000
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "RawAssets", "processed", "audio")
rng = np.random.default_rng(1234)


# Building blocks ---------------------------------------------------------------------------------------------

def t_axis(seconds):
    return np.arange(int(round(seconds * RATE))) / RATE


def white(n):
    return rng.standard_normal(n)


def pink(n):
    """1/f noise by filtering white noise in the frequency domain."""
    spectrum = np.fft.rfft(white(n))
    f = np.fft.rfftfreq(n, 1 / RATE)
    f[0] = f[1]
    return np.fft.irfft(spectrum / np.sqrt(f), n)


def brown(n):
    x = np.cumsum(white(n))
    x -= np.convolve(x, np.ones(4801) / 4801, mode="same")  # remove the drift
    return x


def band(x, low, high):
    """Brick-wall band pass in the frequency domain (fine for noise beds)."""
    spectrum = np.fft.rfft(x)
    f = np.fft.rfftfreq(len(x), 1 / RATE)
    gain = np.clip((f - low * 0.8) / (low * 0.4 + 1e-9), 0, 1) * np.clip((high * 1.25 - f) / (high * 0.5), 0, 1)
    return np.fft.irfft(spectrum * gain, len(x))


def lowpass(x, cutoff):
    """One-pole low pass; cutoff may vary per sample."""
    cutoff = np.broadcast_to(cutoff, x.shape)
    a = np.exp(-2 * np.pi * cutoff / RATE)
    y = np.empty_like(x)
    state = 0.0
    for i in range(len(x)):
        state = a[i] * state + (1 - a[i]) * x[i]
        y[i] = state
    return y


def smooth_noise(n, rate_hz, seed_scale=1.0):
    """Slowly varying random curve 0..1 (for gusts, flutter)."""
    points = max(4, int(n / RATE * rate_hz) + 4)
    knots = rng.random(points) * seed_scale
    x = np.linspace(0, points - 3, n)
    i = np.floor(x).astype(int)
    f = x - i
    f = f * f * (3 - 2 * f)
    return knots[i] * (1 - f) + knots[i + 1] * f


def env(n, attack, decay, curve=4.0):
    t = np.arange(n) / RATE
    a = np.clip(t / max(attack, 1e-4), 0, 1)
    d = np.exp(-np.maximum(t - attack, 0) * curve / max(decay, 1e-4))
    return a * d


def modes(seconds, freqs, decays, amps):
    """A struck object: decaying sine modes."""
    t = t_axis(seconds)
    out = np.zeros_like(t)
    for f, d, a in zip(freqs, decays, amps):
        out += a * np.sin(2 * np.pi * f * t + rng.random() * 6.28) * np.exp(-t / d)
    return out


def normalize(x, peak=0.9):
    m = np.max(np.abs(x))
    return x * (peak / m) if m > 0 else x


def loop_seamless(x, fade=1.0):
    """Crossfades the last `fade` seconds into the start, so the sound loops without a click."""
    k = int(fade * RATE)
    head, body, tail = x[:k], x[k:-k], x[-k:]
    w = np.linspace(0, 1, k)
    if x.ndim == 2:
        w = w[:, None]
    return np.concatenate([tail * (1 - w) + head * w, body])


def save(name, x, stereo=False):
    os.makedirs(OUT, exist_ok=True)
    x = np.asarray(x, dtype=np.float64)
    data = (np.clip(x, -1, 1) * 32767).astype(np.int16)
    with wave.open(os.path.join(OUT, f"{name}.wav"), "wb") as f:
        f.setnchannels(2 if stereo else 1)
        f.setsampwidth(2)
        f.setframerate(RATE)
        f.writeframes(data.tobytes())
    print(f"FWAUDIO {name}: {len(x) / RATE:.2f} s")


# Ambience ----------------------------------------------------------------------------------------------------

def wind_loop(seconds=40.0):
    """Alpine wind: a low roar and a whistling band that swell with slow gusts; left and right differ."""
    n = int(seconds * RATE)
    channels = []
    for side in range(2):
        gust = smooth_noise(n, 0.18) ** 1.6
        flutter = smooth_noise(n, 3.0)
        roar = band(brown(n), 40, 500) * (0.4 + 0.9 * gust)
        whistle = band(pink(n), 500, 2600) * (0.08 + 0.55 * gust ** 2) * (0.7 + 0.3 * flutter)
        channels.append(normalize(roar) * 0.8 + normalize(whistle) * 0.35)
    x = np.stack(channels, axis=1)
    return normalize(loop_seamless(x, 2.0), 0.7)


def gust(seconds=4.5):
    n = int(seconds * RATE)
    e = np.sin(np.linspace(0, np.pi, n)) ** 2
    sweep = band(pink(n), 200, 1800) * e
    roar = band(brown(n), 50, 400) * e
    return normalize(sweep * 0.6 + normalize(roar) * 0.7, 0.8)


def forest_loop(seconds=30.0):
    """Leaves rustling: high noise chopped into many tiny bursts, swelling with the breeze."""
    n = int(seconds * RATE)
    channels = []
    for side in range(2):
        breeze = smooth_noise(n, 0.3) ** 1.3
        grains = np.zeros(n)
        count = int(seconds * 260)
        for start in rng.integers(0, n - 2400, count):
            length = rng.integers(300, 2400)
            grains[start:start + length] += np.hanning(length) * rng.uniform(0.2, 1.0)
        leaves = band(white(n), 1800, 9000) * grains * (0.25 + breeze)
        sway = band(pink(n), 300, 1500) * (0.1 + 0.5 * breeze)
        channels.append(normalize(leaves) * 0.6 + normalize(sway) * 0.3)
    return normalize(loop_seamless(np.stack(channels, axis=1), 2.0), 0.6)


def bird(kind):
    """Short songs of a few mountain birds, made of FM chirps."""
    t_total = {0: 1.6, 1: 2.2, 2: 1.2, 3: 2.6}[kind]
    n = int(t_total * RATE)
    out = np.zeros(n)
    if kind == 0:  # repeated rising "tsee" notes
        notes = [(0.1 * i, 0.08, 4200, 6200) for i in range(rng.integers(5, 9))]
    elif kind == 1:  # falling trill
        notes = [(0.06 * i, 0.05, 5200 - 120 * i, 4400 - 120 * i) for i in range(16)]
    elif kind == 2:  # two whistled notes (like a tit: "tee-chu")
        notes = [(0.05, 0.18, 3600, 3700), (0.32, 0.22, 2600, 2400), (0.65, 0.18, 3600, 3700), (0.92, 0.22, 2600, 2400)]
    else:  # warbling song
        notes = [(0.09 * i, 0.07, rng.uniform(2800, 5000), rng.uniform(2800, 5000)) for i in range(24)]
    for start, length, f0, f1 in notes:
        k = int(length * RATE)
        s = int(start * RATE)
        if s + k >= n:
            break
        tt = np.arange(k) / RATE
        freq = np.linspace(f0, f1, k) + 180 * np.sin(2 * np.pi * 38 * tt)
        phase = 2 * np.pi * np.cumsum(freq) / RATE
        out[s:s + k] += np.sin(phase) * np.sin(np.linspace(0, np.pi, k)) ** 1.5
    return normalize(out, 0.7)


# Jumps -------------------------------------------------------------------------------------------------------

def jump():
    n = int(0.25 * RATE)
    return normalize(band(white(n), 400, 3000) * env(n, 0.01, 0.12, 5) * 0.6 + band(white(n), 60, 300) * env(n, 0.003, 0.05, 6), 0.6)


# Factory -----------------------------------------------------------------------------------------------------

def belt_loop(seconds=4.0):
    """Conveyor: rubber band rumble, the motor's hum and a roller tick every 0.25 s (4 m/s band, 1 m rollers)."""
    n = int(seconds * RATE)
    t = t_axis(seconds)
    rumble = band(brown(n), 40, 300)
    hum = 0.3 * np.sin(2 * np.pi * 100 * t) + 0.15 * np.sin(2 * np.pi * 200 * t) + 0.05 * np.sin(2 * np.pi * 300 * t)
    ticks = np.zeros(n)
    for i in range(int(seconds / 0.25)):
        s = max(0, int((i * 0.25 + rng.uniform(-0.004, 0.004)) * RATE))
        k = int(0.05 * RATE)
        ticks[s:s + k] += modes(k / RATE, [1400 + rng.uniform(-80, 80), 2600], [0.008, 0.005], [1, 0.5])[:max(0, min(k, n - s))]
    x = normalize(rumble) * 0.6 + hum * 0.5 + normalize(ticks) * 0.25
    return normalize(loop_seamless(x, 0.5), 0.7)


def servo(seconds=0.7, up=True):
    """Inserter arm: a geared servo whine rising (or falling) with the move."""
    n = int(seconds * RATE)
    shape = np.sin(np.linspace(0, np.pi, n))
    f = (900 + 600 * (np.linspace(0, 1, n) if up else np.linspace(1, 0, n))) * (1 + 0.02 * np.sin(np.linspace(0, 60, n)))
    phase = 2 * np.pi * np.cumsum(f) / RATE
    whine = np.sign(np.sin(phase)) * 0.3 + np.sin(2 * phase) * 0.3
    gears = band(white(n), 2000, 6000) * 0.2
    return normalize(lowpass(whine + gears, 4000) * shape, 0.6)


def clunk():
    k = 0.3
    return normalize(modes(k, [220, 530, 1150], [0.06, 0.04, 0.02], [1, 0.7, 0.4]) + band(white(int(k * RATE)), 300, 3000) * env(int(k * RATE), 0.001, 0.03, 6), 0.8)


def chest(opening):
    """Wooden chest: creak of the hinge and the lid knocking."""
    n = int(0.6 * RATE)
    t = t_axis(0.6)
    creak_f = 300 + 200 * np.sin(np.linspace(0, 3, n)) * (1 if opening else -1)
    creak = np.sign(np.sin(2 * np.pi * np.cumsum(creak_f) / RATE)) * band(white(n), 200, 2000) * 0.5
    creak *= np.clip(np.sin(np.linspace(0, np.pi, n)), 0, 1) * 0.5
    knock = np.zeros(n)
    s = int((0.4 if not opening else 0.05) * RATE)
    k = n - s
    knock[s:] = modes(k / RATE, [140, 310, 620], [0.05, 0.03, 0.015], [1, 0.6, 0.3])
    return normalize(creak + knock * 1.2, 0.8)


def pole_hum(seconds=3.0):
    """Power line hum: 50 Hz and its odd harmonics, a little corona crackle."""
    t = t_axis(seconds)
    n = len(t)
    hum = sum(a * np.sin(2 * np.pi * f * t) for f, a in ((100, 1.0), (150, 0.5), (250, 0.3), (350, 0.15), (450, 0.1)))
    crackle = np.zeros(n)
    for s in rng.integers(0, n - 200, 120):
        crackle[s:s + 60] += white(60) * rng.uniform(0.1, 0.6)
    return normalize(loop_seamless(hum * (1 + 0.05 * smooth_noise(n, 1)) + band(crackle, 2000, 8000), 0.5), 0.5)


def turbine_loop(seconds=6.0):
    """Wind turbine: a blade passes the tower every 1.5 s with a whoosh; the nacelle's low drone underneath."""
    n = int(seconds * RATE)
    t = t_axis(seconds)
    whoosh = band(pink(n), 150, 1200) * (0.2 + 0.8 * (0.5 + 0.5 * np.cos(2 * np.pi * t / 1.5)) ** 4)
    drone = 0.4 * np.sin(2 * np.pi * 48 * t) + 0.2 * np.sin(2 * np.pi * 96 * t) + band(brown(n), 30, 120) * 0.5
    return normalize(loop_seamless(normalize(whoosh) * 0.8 + drone * 0.6, 0.5), 0.6)


# Interface and building --------------------------------------------------------------------------------------

def click(freq=2200, length=0.04):
    k = int(length * RATE)
    return normalize(modes(length, [freq, freq * 2.3], [0.006, 0.003], [1, 0.4]) + band(white(k), 3000, 9000) * env(k, 0.0005, 0.005, 6) * 0.3, 0.5)


def hover():
    return click(3400, 0.025) * 0.5


def place():
    """Something heavy set down: thump and clank of steel."""
    k = 0.45
    n = int(k * RATE)
    return normalize(band(white(n), 40, 250) * env(n, 0.002, 0.1, 5) + modes(k, [180, 420, 900, 1700], [0.12, 0.08, 0.05, 0.03], [0.6, 0.8, 0.5, 0.3]), 0.85)


def rotate():
    k = 0.18
    n = int(k * RATE)
    return normalize(modes(k, [700, 1500], [0.03, 0.02], [1, 0.5]) + band(white(n), 1500, 6000) * env(n, 0.001, 0.05, 6) * 0.5, 0.5)


def pickup():
    k = 0.35
    n = int(k * RATE)
    t = t_axis(k)
    rise = np.sin(2 * np.pi * np.cumsum(np.linspace(500, 1400, n)) / RATE) * env(n, 0.005, 0.2, 4)
    return normalize(rise * 0.5 + band(white(n), 800, 5000) * env(n, 0.002, 0.08, 5) * 0.6, 0.6)


def mine_hit(variant):
    """Pickaxe on rock: sharp metal-on-stone strike and falling chips."""
    k = 0.6
    n = int(k * RATE)
    strike = modes(k, rng.uniform([1800, 3100, 4700], [2100, 3400, 5200]), [0.03, 0.02, 0.012], [1, 0.7, 0.4])
    crack = band(white(n), 1000, 9000) * env(n, 0.0005, 0.04, 6)
    chips = np.zeros(n)
    for s in rng.integers(int(0.05 * RATE), int(0.45 * RATE), 14):
        m = int(0.03 * RATE)
        chips[s:s + m] += modes(0.03, [rng.uniform(2500, 6000)], [0.008], [1])[:min(m, n - s)] * rng.uniform(0.1, 0.4)
    thud = band(white(n), 60, 300) * env(n, 0.002, 0.06, 6)
    return normalize(normalize(strike) * 0.6 + normalize(crack) * 0.6 + chips + normalize(thud) * 0.5, 0.9)


def invalid():
    k = 0.3
    t = t_axis(k)
    n = len(t)
    tone = (np.sign(np.sin(2 * np.pi * 180 * t)) * 0.4 + np.sin(2 * np.pi * 120 * t) * 0.6) * env(n, 0.005, 0.25, 3)
    return normalize(lowpass(tone, 1800), 0.5)


def confirm():
    k = 0.6
    t = t_axis(k)
    n = len(t)
    out = np.zeros(n)
    for i, f in enumerate((523.25, 659.25, 783.99)):
        s = int(i * 0.07 * RATE)
        m = n - s
        tt = np.arange(m) / RATE
        out[s:] += (np.sin(2 * np.pi * f * tt) + 0.3 * np.sin(4 * np.pi * f * tt)) * env(m, 0.005, 0.4, 4)
    return normalize(out, 0.5)



# The sorting game at the garage bin: a piece dropping into its bin (metal, plastic, glass), into the wrong one, and the
# bag done ----------------------------------------------------------------------------------------------------------

def bin_drop():
    """The thud of the bin's plastic body as something lands in it."""
    k = 0.35
    n = int(k * RATE)
    return band(white(n), 70, 400) * env(n, 0.002, 0.12, 5) + modes(k, [95, 160, 240], [0.09, 0.06, 0.04], [0.5, 0.35, 0.2])


def sort_metal():
    """An empty can or tin landing in the bin: a bright, ringing clatter that bounces once."""
    k = 0.7
    n = int(k * RATE)
    out = normalize(bin_drop()) * 0.45
    out = np.concatenate([out, np.zeros(n - len(out))])
    for start, level in ((0.0, 1.0), (0.11, 0.45), (0.19, 0.2)):
        s = int(start * RATE)
        hit = modes(k - start, [1180, 2310, 3470, 5120, 6890], [0.16, 0.11, 0.08, 0.05, 0.03], [1, 0.8, 0.6, 0.35, 0.2])
        hit += band(white(len(hit)), 2500, 9000) * env(len(hit), 0.0005, 0.01, 6) * 0.6
        out[s:] += normalize(hit) * level * 0.7
    return normalize(out, 0.75)


def sort_plastic():
    """A light bottle or cup: a hollow, dull knock and a short rattle."""
    k = 0.45
    n = int(k * RATE)
    out = normalize(bin_drop()) * 0.55
    out = np.concatenate([out, np.zeros(n - len(out))])
    for start, level in ((0.0, 1.0), (0.07, 0.4)):
        s = int(start * RATE)
        hit = modes(k - start, [410, 690, 1130], [0.035, 0.025, 0.015], [1, 0.7, 0.4])
        hit += band(white(len(hit)), 600, 3500) * env(len(hit), 0.001, 0.03, 5) * 0.8
        out[s:] += normalize(hit) * level * 0.6
    return normalize(out, 0.7)


def sort_glass():
    """A bottle or jar onto the others: a clear clink that rings on."""
    k = 1.1
    n = int(k * RATE)
    out = normalize(bin_drop()) * 0.3
    out = np.concatenate([out, np.zeros(n - len(out))])
    for start, level, base in ((0.0, 1.0, 2630), (0.06, 0.5, 3120)):
        s = int(start * RATE)
        hit = modes(k - start, [base, base * 2.32, base * 4.25, base * 6.63], [0.42, 0.25, 0.14, 0.08], [1, 0.55, 0.3, 0.15])
        hit += band(white(len(hit)), 4000, 12000) * env(len(hit), 0.0003, 0.006, 6) * 0.5
        out[s:] += normalize(hit) * level * 0.6
    return normalize(out, 0.6)


def sort_wrong():
    """Not this bin: a soft low double buzz."""
    k = 0.36
    t = t_axis(k)
    n = len(t)
    tone = (np.sin(2 * np.pi * 150 * t) + 0.5 * np.sign(np.sin(2 * np.pi * 150 * t)) * 0.4) * env(n, 0.004, 0.12, 4)
    second = np.roll(tone, int(0.15 * RATE))
    second[:int(0.15 * RATE)] = 0
    return normalize(lowpass(tone + second * 0.8, 1400), 0.45)


def sort_done():
    """The bag is sorted: a rising arpeggio with a little shimmer."""
    k = 1.0
    t = t_axis(k)
    n = len(t)
    out = np.zeros(n)
    for i, f in enumerate((523.25, 659.25, 783.99, 1046.5)):
        s = int(i * 0.075 * RATE)
        m = n - s
        tt = np.arange(m) / RATE
        out[s:] += (np.sin(2 * np.pi * f * tt) + 0.25 * np.sin(4 * np.pi * f * tt) + 0.1 * np.sin(6 * np.pi * f * tt)) * env(m, 0.004, 0.55, 4)
    out += band(white(n), 6000, 12000) * env(n, 0.25, 0.5, 4) * 0.04
    return normalize(out, 0.5)


def sort_pick():
    """A piece taken out of the bag: a soft rustle of the sack."""
    k = 0.18
    n = int(k * RATE)
    return normalize(band(white(n), 1200, 7000) * env(n, 0.01, 0.12, 4) * smooth_noise(n, 60), 0.35)


# Write everything --------------------------------------------------------------------------------------------
save("amb_wind_loop", wind_loop(), stereo=True)
for i in range(3):
    save(f"amb_gust_{i + 1}", gust(rng.uniform(3.5, 6.0)))
save("amb_forest_loop", forest_loop(), stereo=True)
for i in range(4):
    save(f"amb_bird_{i + 1}", bird(i))
save("jump", jump())
save("belt_loop", belt_loop())
save("inserter_up", servo(0.6, True))
save("inserter_down", servo(0.6, False))
save("inserter_clunk", clunk())
save("chest_open", chest(True))
save("chest_close", chest(False))
save("pole_hum_loop", pole_hum())
save("turbine_loop", turbine_loop())
save("ui_click", click())
save("ui_hover", hover())
save("build_place", place())
save("build_rotate", rotate())
save("build_pickup", pickup())
for i in range(3):
    save(f"mine_hit_{i + 1}", mine_hit(i))
save("build_invalid", invalid())
save("ui_confirm", confirm())
save("sort_metal", sort_metal())
save("sort_plastic", sort_plastic())
save("sort_glass", sort_glass())
save("sort_wrong", sort_wrong())
save("sort_done", sort_done())
save("sort_pick", sort_pick())
