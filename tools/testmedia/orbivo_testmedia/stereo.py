"""Stereo direction test video for sound fixed to the picture (M7).

The player plays the two stereo channels from two virtual speakers in the picture, at +-60 deg from the front
(default width). This video marks those two places on the horizon and plays noise bursts in a 6 s cycle:
    0-2 s  left channel only   -> the box "L" (60 deg left of the front) lights up
    2-4 s  right channel only  -> the box "R" (60 deg right of the front) lights up
    4-6 s  both channels, the same noise -> both boxes light up, the sound comes from the front
With the sound fixed to the picture, the bursts must come from the lit box wherever the head points.
"""

from __future__ import annotations

import subprocess
import time
from pathlib import Path

import numpy as np

from .conventions import equirect_pixel
from .faces import FONT_BOLD
from .video import VideoSettings, _ff_path

SAMPLE_RATE = 48_000
SPEAKER_DEG = 60.0  # yaw of the left speaker (positive = left), the player's default width
CYCLE_S = 6.0
SEGMENT_S = 2.0
DURATION_S = 36.0
BURST_ON_S = 0.30
BURST_PERIOD_S = 0.50
FADE_S = 0.005
LEVEL = 0.3  # peak of the noise
BOX_DEG = 12.0
LABEL_LAT_DEG = -18.0

SEGMENTS = (
    ("left", "nur links", True, False),
    ("right", "nur rechts", False, True),
    ("both", "beide Kanäle (Mitte)", True, True),
)


def build_audio(duration_s: float = DURATION_S, seed: int = 7) -> np.ndarray:
    """Stereo samples (2, frames): white noise bursts on the channels of each segment, the same noise on both."""
    frames = round(duration_s * SAMPLE_RATE)
    t = np.arange(frames) / SAMPLE_RATE
    noise = np.random.default_rng(seed).uniform(-1.0, 1.0, frames)

    # Bursts with 5 ms raised-cosine edges, so the test itself does not click.
    phase = np.mod(t, BURST_PERIOD_S)
    envelope = np.where(phase < BURST_ON_S, 1.0, 0.0)
    rise = phase < FADE_S
    envelope[rise] = 0.5 - 0.5 * np.cos(np.pi * phase[rise] / FADE_S)
    fall = (phase >= BURST_ON_S - FADE_S) & (phase < BURST_ON_S)
    envelope[fall] = 0.5 - 0.5 * np.cos(np.pi * (BURST_ON_S - phase[fall]) / FADE_S)
    signal = LEVEL * noise * envelope

    segment = (np.mod(t, CYCLE_S) // SEGMENT_S).astype(int)
    left_on = np.array([s[2] for s in SEGMENTS])[segment]
    right_on = np.array([s[3] for s in SEGMENTS])[segment]
    return np.stack([signal * left_on, signal * right_on]).astype(np.float32)


def events(duration_s: float = DURATION_S) -> list[dict]:
    """The segments for the manifest."""
    result = []
    start = 0.0
    while start < duration_s - 1e-9:
        key, _, left, right = SEGMENTS[int(round(start / SEGMENT_S)) % len(SEGMENTS)]
        result.append({"start_s": start, "end_s": start + SEGMENT_S, "segment": key, "left": left, "right": right})
        start += SEGMENT_S
    return result


def _segment_active(index: int) -> str:
    """ffmpeg expression: true while segment `index` of the cycle plays."""
    return f"between(mod(t,{CYCLE_S:g}),{index * SEGMENT_S:g},{(index + 1) * SEGMENT_S - 0.001:g})"


def _filter_graph(width: int, height: int, settings: VideoSettings) -> str:
    fps = settings.fps
    work_format = "yuv420p10le" if settings.bit_depth == 10 else "yuv420p"
    box_w = round(BOX_DEG * width / 360.0)
    box_h = round(BOX_DEG * height / 180.0)
    font_size = max(16, round(0.6 * box_h))
    label_size = max(12, round(0.25 * box_h))
    chain = [
        f"[0:v]scale=out_color_matrix=bt709:out_range=tv,format={work_format},"
        f"loop=loop=-1:size=1:start=0,setpts=N/({fps}*TB)"
    ]
    font = _ff_path(FONT_BOLD)
    # Speaker "L" sits at yaw +60 (left), which is longitude -60 in the equirect image.
    for name, lon, segments in (("L", -SPEAKER_DEG, (0, 2)), ("R", SPEAKER_DEG, (1, 2))):
        x, y = equirect_pixel(lon, 0.0, width, height)
        x0, y0 = round(x - box_w / 2), round(y - box_h / 2)
        lit = "+".join(_segment_active(i) for i in segments)
        chain.append(f"drawbox=x={x0}:y={y0}:w={box_w}:h={box_h}:color=black@0.7:t=fill")
        chain.append(f"drawbox=x={x0}:y={y0}:w={box_w}:h={box_h}:color=lime:t=fill:enable='{lit}'")
        chain.append(f"drawbox=x={x0}:y={y0}:w={box_w}:h={box_h}:color=white:t={max(3, box_w // 30)}")
        chain.append(
            f"drawtext=fontfile={font}:text='{name}':fontsize={font_size}:fontcolor=white:borderw=3:bordercolor=black"
            f":x={round(x)}-text_w/2:y={round(y)}-text_h/2")
        _, label_y = equirect_pixel(lon, LABEL_LAT_DEG, width, height)
        chain.append(
            f"drawtext=fontfile={font}:text='Lautsprecher {name} ({abs(SPEAKER_DEG):g}°)':fontsize={label_size}"
            f":fontcolor=white:box=1:boxcolor=black@0.65:boxborderw=6:x={round(x)}-text_w/2:y={round(label_y)}-text_h/2")

    # What plays right now, below the front.
    fx, fy = equirect_pixel(0.0, LABEL_LAT_DEG, width, height)
    for index, (_, text, _, _) in enumerate(SEGMENTS):
        chain.append(
            f"drawtext=fontfile={font}:text='Ton – {text}':fontsize={label_size}:fontcolor=white:box=1"
            f":boxcolor=black@0.65:boxborderw=6:x={round(fx)}-text_w/2:y={round(fy)}-text_h/2"
            f":enable='{_segment_active(index)}'")

    chain.append(
        f"setparams=colorspace=bt709:color_primaries=bt709:color_trc=bt709:range=tv,format={settings.pix_fmt}[v]")
    return ",".join(chain)


def encode(ffmpeg: Path, still: Path, audio: Path, output: Path, width: int, height: int,
           settings: VideoSettings) -> float:
    """Encodes the test video from a mono equirect still and the stereo WAV; returns the elapsed time."""
    codec = settings.codec
    frames = round(settings.fps * DURATION_S)
    command = [
        str(ffmpeg), "-hide_banner", "-nostdin", "-y",
        "-framerate", str(settings.fps), "-i", str(still),
        "-i", str(audio),
        "-filter_complex", _filter_graph(width, height, settings),
        "-map", "[v]", "-map", "1:a",
        "-frames:v", str(frames), "-r", str(settings.fps), "-fps_mode", "cfr",
        "-c:v", codec.name, *codec.args, *(codec.extra_10 if settings.bit_depth == 10 else ()),
        "-g", str(settings.fps),
        "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
        "-c:a", "aac", "-b:a", "256k", "-t", f"{DURATION_S:g}",
        "-movflags", "+faststart",
    ]
    if codec.tag:
        command += ["-tag:v", codec.tag]
    command.append(str(output))

    started = time.monotonic()
    result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if result.returncode != 0:
        raise RuntimeError(f"ffmpeg failed for {output.name} (exit {result.returncode}):\n{result.stderr[-3000:]}")
    return time.monotonic() - started
