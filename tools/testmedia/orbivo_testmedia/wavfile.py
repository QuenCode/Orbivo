"""Minimal WAV reading/writing without extra dependencies.

Multichannel files are written as WAVE_FORMAT_EXTENSIBLE with channel mask 0, the usual way to say
"these channels are not loudspeaker feeds" (as for Ambisonics B-format)."""

from __future__ import annotations

import struct
import wave
from pathlib import Path

import numpy as np

WAVE_FORMAT_EXTENSIBLE = 0xFFFE
KSDATAFORMAT_SUBTYPE_PCM = bytes.fromhex("0100000000001000800000aa00389b71")


def write_wav(path: Path, samples: np.ndarray, sample_rate: int, bits: int = 24, channel_mask: int = 0) -> None:
    """Writes `samples` (channels, frames) in the range -1..1 as integer PCM."""
    if samples.ndim != 2:
        raise ValueError("samples must have shape (channels, frames)")
    channels = samples.shape[0]
    interleaved = np.ascontiguousarray(np.clip(samples, -1.0, 1.0).T)

    if bits == 24:
        ints = np.round(interleaved * 8388607.0).astype("<i4")
        data = ints.view(np.uint8).reshape(-1, 4)[:, :3].tobytes()
    elif bits == 16:
        data = np.round(interleaved * 32767.0).astype("<i2").tobytes()
    else:
        raise ValueError("bits must be 16 or 24")

    block_align = channels * bits // 8
    fmt = struct.pack("<HHIIHHHHI16s", WAVE_FORMAT_EXTENSIBLE, channels, sample_rate, sample_rate * block_align,
                      block_align, bits, 22, bits, channel_mask, KSDATAFORMAT_SUBTYPE_PCM)
    with open(path, "wb") as stream:
        stream.write(b"RIFF")
        stream.write(struct.pack("<I", 4 + (8 + len(fmt)) + (8 + len(data))))
        stream.write(b"WAVE")
        stream.write(b"fmt " + struct.pack("<I", len(fmt)) + fmt)
        stream.write(b"data" + struct.pack("<I", len(data)) + data)


def read_wav_mono16(path: Path) -> tuple[np.ndarray, int]:
    """Reads a 16-bit PCM mono WAV (as written by Windows TTS) into floats -1..1."""
    with wave.open(str(path), "rb") as source:
        if source.getsampwidth() != 2 or source.getnchannels() != 1:
            raise ValueError(f"{path.name}: expected 16-bit mono PCM")
        frames = source.readframes(source.getnframes())
        return np.frombuffer(frames, dtype="<i2").astype(np.float32) / 32768.0, source.getframerate()
