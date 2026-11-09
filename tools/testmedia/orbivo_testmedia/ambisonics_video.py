"""Ambisonics direction test video (M10).

The sound is the Ambisonics direction test (ambisonics.build_direction_test): for each direction a spoken word and
three noise bursts, encoded at that direction. The picture is the mono equirect test image with a box at every
horizontal direction and a cap at the zenith and the nadir. The box (or cap) of the direction that plays lights up,
and a line below the front says which direction it is. With the sound field fixed to the picture, the sound must come
from the lit box wherever the head points.
"""

from __future__ import annotations

import subprocess
import time
from pathlib import Path

from .conventions import AUDIO_DIRECTIONS, equirect_pixel
from .faces import FONT_BOLD
from .video import VideoSettings, _ff_path

BOX_DEG = 12.0
CAP_DEG = 12.0  # the up/down caps cover the last 12 deg of latitude
LABEL_LAT_DEG = -18.0
TAIL_S = 1.0  # extra silence after the last direction


def _active(event: dict) -> tuple[float, float]:
    """Start and end of a direction in seconds: from the spoken word to the end of the last burst."""
    start = event["speech_s"][0] if "speech_s" in event else event["noise_bursts_s"][0]
    end = event["noise_bursts_s"][-1] + 0.2
    return start, end


def _enable(event: dict) -> str:
    start, end = _active(event)
    return f"between(t,{start:.3f},{end:.3f})"


def _filter_graph(width: int, height: int, settings: VideoSettings, events: list[dict]) -> str:
    fps = settings.fps
    work_format = "yuv420p10le" if settings.bit_depth == 10 else "yuv420p"
    box_w = round(BOX_DEG * width / 360.0)
    box_h = round(BOX_DEG * height / 180.0)
    cap_h = round(CAP_DEG * height / 180.0)
    border = max(3, box_w // 30)
    font_size = max(14, round(0.3 * box_h))
    label_size = max(12, round(0.25 * box_h))
    font = _ff_path(FONT_BOLD)
    by_key = {event["direction"]: event for event in events}
    chain = [
        f"[0:v]scale=out_color_matrix=bt709:out_range=tv,format={work_format},"
        f"loop=loop=-1:size=1:start=0,setpts=N/({fps}*TB)"
    ]

    for direction in AUDIO_DIRECTIONS:
        event = by_key[direction.key]
        lit = _enable(event)
        if abs(direction.pitch_deg) >= 89.0:
            # Zenith or nadir: a cap over the whole width (a disk around the pole in the headset).
            y0 = 0 if direction.pitch_deg > 0 else height - cap_h
            chain.append(f"drawbox=x=0:y={y0}:w={width}:h={cap_h}:color=lime@0.85:t=fill:enable='{lit}'")
            continue
        # Equirect longitude is minus yaw: "links" (yaw +90) lies left of the image center.
        lon = -direction.yaw_deg
        x, y = equirect_pixel(lon, direction.pitch_deg, width, height)
        centers = [x]
        if abs(abs(lon) - 180.0) < 1e-6:
            centers = [0.0, float(width)]  # behind: the box is split over both image edges
        for cx in centers:
            x0, y0 = round(cx - box_w / 2), round(y - box_h / 2)
            chain.append(f"drawbox=x={x0}:y={y0}:w={box_w}:h={box_h}:color=black@0.7:t=fill")
            chain.append(f"drawbox=x={x0}:y={y0}:w={box_w}:h={box_h}:color=lime:t=fill:enable='{lit}'")
            chain.append(f"drawbox=x={x0}:y={y0}:w={box_w}:h={box_h}:color=white:t={border}")
            chain.append(
                f"drawtext=fontfile={font}:text='{direction.spoken}':fontsize={font_size}:fontcolor=white:borderw=3"
                f":bordercolor=black:x={round(cx)}-text_w/2:y={round(y)}-text_h/2")

    fx, fy = equirect_pixel(0.0, LABEL_LAT_DEG, width, height)
    for direction in AUDIO_DIRECTIONS:
        chain.append(
            f"drawtext=fontfile={font}:text='Ton von – {direction.spoken}':fontsize={label_size}:fontcolor=white"
            f":box=1:boxcolor=black@0.65:boxborderw=6:x={round(fx)}-text_w/2:y={round(fy)}-text_h/2"
            f":enable='{_enable(by_key[direction.key])}'")

    chain.append(
        f"setparams=colorspace=bt709:color_primaries=bt709:color_trc=bt709:range=tv,format={settings.pix_fmt}[v]")
    return ",".join(chain)


def encode(ffmpeg: Path, still: Path, audio: Path, output: Path, width: int, height: int,
           settings: VideoSettings, events: list[dict], audio_args: list[str]) -> float:
    """Encodes the test video from a mono equirect still and an Ambisonics WAV; returns the elapsed time.
    `audio_args` choose the sound codec, e.g. ["-c:a", "aac", "-b:a", "256k"]."""
    codec = settings.codec
    frames = round(settings.fps * settings.duration_s)
    command = [
        str(ffmpeg), "-hide_banner", "-nostdin", "-y",
        "-framerate", str(settings.fps), "-i", str(still),
        "-i", str(audio),
        "-filter_complex", _filter_graph(width, height, settings, events),
        "-map", "[v]", "-map", "1:a",
        "-frames:v", str(frames), "-r", str(settings.fps), "-fps_mode", "cfr",
        "-c:v", codec.name, *codec.args, *(codec.extra_10 if settings.bit_depth == 10 else ()),
        "-g", str(settings.fps),
        "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
        *audio_args, "-t", f"{settings.duration_s:g}",
    ]
    if output.suffix == ".mp4":
        command += ["-movflags", "+faststart"]
    if codec.tag:
        command += ["-tag:v", codec.tag]
    command.append(str(output))

    started = time.monotonic()
    result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if result.returncode != 0:
        raise RuntimeError(f"ffmpeg failed for {output.name} (exit {result.returncode}):\n{result.stderr[-3000:]}")
    return time.monotonic() - started
