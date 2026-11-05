"""First-order Ambisonics direction test (AmbiX and FuMa).

For each direction in conventions.AUDIO_DIRECTIONS: a spoken German word ("vorne", "links", ...)
followed by three pink-noise bursts, all encoded at that direction. Noise localizes better than speech;
the word tells the listener where the sound should come from.
"""

from __future__ import annotations

import math

import numpy as np

from .conventions import AUDIO_DIRECTIONS, AudioDirection, direction_vector, openxr_to_ambisonics_axes

SAMPLE_RATE = 48000
LEAD_IN_S = 1.0
GAP_AFTER_SPEECH_S = 0.3
BURST_S = 0.2
BURST_GAP_S = 0.15
BURST_COUNT = 3
PAUSE_AFTER_DIRECTION_S = 1.2
SPEECH_PEAK = 0.5
NOISE_PEAK = 0.4
FADE_S = 0.005


def foa_gains_ambix(direction: AudioDirection) -> np.ndarray:
    """ACN/SN3D first-order gains (W, Y, Z, X) for a plane wave from `direction`."""
    x_amb, y_amb, z_amb = openxr_to_ambisonics_axes(*direction_vector(direction.yaw_deg, direction.pitch_deg))
    return np.array([1.0, y_amb, z_amb, x_amb], dtype=np.float64)


def ambix_to_fuma(ambix: np.ndarray) -> np.ndarray:
    """First order only: ACN/SN3D (W, Y, Z, X) -> FuMa (W, X, Y, Z) with W scaled by 1/sqrt(2)."""
    w, y, z, x = ambix
    return np.stack([w / math.sqrt(2.0), x, y, z])


def _fade(signal: np.ndarray, sample_rate: int) -> np.ndarray:
    n = min(len(signal) // 2, int(FADE_S * sample_rate))
    if n > 0:
        ramp = np.linspace(0.0, 1.0, n, dtype=np.float64)
        signal = signal.copy()
        signal[:n] *= ramp
        signal[-n:] *= ramp[::-1]
    return signal


def pink_noise(frames: int, rng: np.random.Generator) -> np.ndarray:
    spectrum = np.fft.rfft(rng.standard_normal(frames))
    frequencies = np.arange(len(spectrum), dtype=np.float64)
    frequencies[0] = 1.0
    noise = np.fft.irfft(spectrum / np.sqrt(frequencies), n=frames)
    return noise / np.max(np.abs(noise))


def trim_silence(signal: np.ndarray, threshold: float = 0.01) -> np.ndarray:
    loud = np.flatnonzero(np.abs(signal) > threshold)
    return signal[loud[0]:loud[-1] + 1] if loud.size else signal


def build_direction_test(speech: dict[str, np.ndarray] | None, seed: int = 1) -> tuple[np.ndarray, list[dict]]:
    """Returns AmbiX samples (4, frames) and the event list for the manifest.
    `speech` maps direction keys to mono float signals at SAMPLE_RATE; None means noise only."""
    rng = np.random.default_rng(seed)
    segments: list[tuple[int, np.ndarray, np.ndarray]] = []  # (start frame, mono, gains)
    events = []
    position = int(LEAD_IN_S * SAMPLE_RATE)

    for direction in AUDIO_DIRECTIONS:
        gains = foa_gains_ambix(direction)
        event = {
            "direction": direction.key,
            "spoken": direction.spoken,
            "yaw_deg": direction.yaw_deg,
            "pitch_deg": direction.pitch_deg,
        }
        if speech is not None:
            word = trim_silence(speech[direction.key])
            word = _fade(word / max(1e-6, np.max(np.abs(word))) * SPEECH_PEAK, SAMPLE_RATE)
            segments.append((position, word, gains))
            event["speech_s"] = [round(position / SAMPLE_RATE, 3), round((position + len(word)) / SAMPLE_RATE, 3)]
            position += len(word) + int(GAP_AFTER_SPEECH_S * SAMPLE_RATE)

        bursts = []
        for _ in range(BURST_COUNT):
            burst = _fade(pink_noise(int(BURST_S * SAMPLE_RATE), rng) * NOISE_PEAK, SAMPLE_RATE)
            segments.append((position, burst, gains))
            bursts.append(round(position / SAMPLE_RATE, 3))
            position += len(burst) + int(BURST_GAP_S * SAMPLE_RATE)
        event["noise_bursts_s"] = bursts
        events.append(event)
        position += int(PAUSE_AFTER_DIRECTION_S * SAMPLE_RATE)

    output = np.zeros((4, position), dtype=np.float64)
    for start, mono, gains in segments:
        output[:, start:start + len(mono)] += gains[:, None] * mono[None, :]
    return output, events
