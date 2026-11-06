"""Encodes test videos from a still test image with ffmpeg.

Per eye, on top of the still image:
- timecode and frame number below VORNE (judder and frame drops become visible),
- a yellow marker that travels once around the horizon per clip (smooth motion test; equirect only,
  in VR180 it is visible in the front half),
- a white square below VORNE that flashes for two frames at every full second, together with
  a 1 kHz beep on the stereo audio track (A/V sync check from M5 on).
Color conversion is explicitly BT.709 limited range; ffmpeg would otherwise use BT.601 for RGB -> YUV.
"""

from __future__ import annotations

import subprocess
import time
from dataclasses import dataclass, field
from pathlib import Path

from .conventions import equirect_pixel
from .equirect import Layout, eye_regions
from .faces import FONT_BOLD, FONT_MONO
from .projections import Projection, eye_point, pixels_per_degree

EQUIRECT360 = Projection("equirect360")

TIMECODE_LAT_DEG = -10.0
TIMECODE_HEIGHT_DEG = 3.0
ORBIT_LAT_DEG = 25.0
FLASH_LAT_DEG = -42.0
MARKER_SIZE_DEG = 3.0
BEEP_HZ = 1000


@dataclass(frozen=True)
class Codec:
    name: str  # ffmpeg encoder
    short: str  # file name tag
    max_size: int  # largest supported width/height
    pix_fmt_8: str
    pix_fmt_10: str | None
    args: tuple[str, ...]
    extra_10: tuple[str, ...] = field(default_factory=tuple)
    tag: str | None = None


CODECS = {
    "hevc_nvenc": Codec("hevc_nvenc", "hevc", 8192, "yuv420p", "p010le",
                        ("-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", "21", "-b:v", "0"),
                        ("-profile:v", "main10"), "hvc1"),
    "av1_nvenc": Codec("av1_nvenc", "av1", 8192, "yuv420p", "p010le",
                       ("-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", "28", "-b:v", "0")),
    "libsvtav1": Codec("libsvtav1", "av1", 8192, "yuv420p", "yuv420p10le", ("-preset", "8", "-crf", "30")),
    "h264_nvenc": Codec("h264_nvenc", "h264", 4096, "yuv420p", None,
                        ("-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", "21", "-b:v", "0"), (), "avc1"),
}


@dataclass(frozen=True)
class VideoSettings:
    codec: Codec
    bit_depth: int
    fps: int
    duration_s: float

    @property
    def frame_count(self) -> int:
        return round(self.fps * self.duration_s)

    @property
    def flash_s(self) -> float:
        # Two frames, rounded to whole milliseconds so the 1 kHz beep ends on a zero crossing.
        return round(2.0 / self.fps, 3)

    @property
    def pix_fmt(self) -> str:
        return self.codec.pix_fmt_10 if self.bit_depth == 10 else self.codec.pix_fmt_8


def _ff_path(path: Path) -> str:
    """Path as an ffmpeg filter option value (forward slashes, escaped drive colon, quoted)."""
    return "'" + path.as_posix().replace(":", r"\:") + "'"


def _filter_graph(layout: Layout, width: int, height: int, settings: VideoSettings,
                  projection: Projection = EQUIRECT360) -> str:
    fps = settings.fps
    font_file = FONT_MONO if FONT_MONO.exists() else FONT_BOLD
    work_format = "yuv420p10le" if settings.bit_depth == 10 else "yuv420p"
    parts = [
        f"[0:v]scale=out_color_matrix=bt709:out_range=tv,format={work_format},"
        f"loop=loop=-1:size=1:start=0,setpts=N/({fps}*TB)[s0]"
    ]
    stage = 0
    for index, (x0, y0, w, h) in enumerate(eye_regions(layout, width, height)):
        if projection.key == "equirect360":
            marker_w = max(4, round(MARKER_SIZE_DEG * w / 360.0))
            marker_h = max(4, round(MARKER_SIZE_DEG * h / 180.0))
            _, orbit_y = equirect_pixel(0.0, ORBIT_LAT_DEG, w, h)
            center_x, text_y = w / 2, equirect_pixel(0.0, TIMECODE_LAT_DEG, w, h)[1]
            flash_x, flash_y = w / 2, equirect_pixel(0.0, FLASH_LAT_DEG, w, h)[1]
            font_size = max(12, round(TIMECODE_HEIGHT_DEG * h / 180.0))
        else:
            density = pixels_per_degree(projection, 0.0, 0.0, w, h)
            marker_w = marker_h = max(4, round(MARKER_SIZE_DEG * density))
            _, orbit_y = eye_point(projection, 0.0, ORBIT_LAT_DEG, w, h)
            center_x, text_y = eye_point(projection, 0.0, TIMECODE_LAT_DEG, w, h)
            flash_x, flash_y = eye_point(projection, 0.0, FLASH_LAT_DEG, w, h)
            font_size = max(12, round(TIMECODE_HEIGHT_DEG * pixels_per_degree(projection, 0.0, TIMECODE_LAT_DEG, w, h)))

        if projection.key in ("equirect360", "equirect180"):
            # Equirect 180 covers half the turn: the marker runs over 2w and is shown while it is inside the eye.
            turn = w if projection.key == "equirect360" else 2 * w
            parts.append(f"color=c=yellow:s={marker_w}x{marker_h}:r={fps}[m{index}]")
            position = f"mod({w}*0.5+t*{turn}/{settings.duration_s},{turn})"
            enable = "" if turn == w else f":enable='lt({position},{w - marker_w})'"
            orbit_x = f"{x0}+min({position},{w - marker_w})"
            parts.append(
                f"[s{stage}][m{index}]overlay=x='{orbit_x}':y={round(y0 + orbit_y - marker_h / 2)}:eval=frame{enable}"
                f"[s{stage + 1}]")
            stage += 1

        parts.append(
            f"[s{stage}]drawtext=fontfile={_ff_path(font_file)}:text='%{{pts\\:hms}}  F%{{frame_num}}'"
            f":fontsize={font_size}:fontcolor=white:box=1:boxcolor=black@0.65:boxborderw={max(2, font_size // 5)}"
            f":x={round(x0 + center_x)}-text_w/2:y={round(y0 + text_y)}-text_h/2,"
            f"drawbox=x={round(x0 + flash_x - marker_w / 2)}:y={round(y0 + flash_y - marker_h / 2)}"
            f":w={marker_w}:h={marker_h}:color=white:t=fill:enable='lt(mod(t,1),{settings.flash_s})'[s{stage + 1}]")
        stage += 1

    final_format = settings.pix_fmt
    parts.append(
        f"[s{stage}]setparams=colorspace=bt709:color_primaries=bt709:color_trc=bt709:range=tv,"
        f"format={final_format}[v]")
    return ";".join(parts)


def _beep_source(settings: VideoSettings) -> str:
    channel = f"if(lt(mod(t,1),{settings.flash_s}),0.5*sin(2*PI*{BEEP_HZ}*t),0)"
    return f"aevalsrc=exprs='{channel}|{channel}':s=48000:d={settings.duration_s}"


def encode(ffmpeg: Path, still: Path, output: Path, layout: Layout, width: int, height: int,
           settings: VideoSettings, projection: Projection = EQUIRECT360) -> float:
    """Encodes one video; returns the elapsed time in seconds. Raises on ffmpeg errors."""
    codec = settings.codec
    command = [
        str(ffmpeg), "-hide_banner", "-nostdin", "-y",
        "-framerate", str(settings.fps), "-i", str(still),
        "-f", "lavfi", "-i", _beep_source(settings),
        "-filter_complex", _filter_graph(layout, width, height, settings, projection),
        "-map", "[v]", "-map", "1:a",
        "-frames:v", str(settings.frame_count), "-r", str(settings.fps), "-fps_mode", "cfr",
        "-c:v", codec.name, *codec.args, *(codec.extra_10 if settings.bit_depth == 10 else ()),
        "-g", str(settings.fps),
        "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
        "-c:a", "aac", "-b:a", "192k", "-t", str(settings.duration_s),
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


def available_encoders(ffmpeg: Path) -> set[str]:
    result = subprocess.run([str(ffmpeg), "-hide_banner", "-encoders"], capture_output=True, text=True, check=True)
    names = set()
    for line in result.stdout.splitlines():
        fields = line.split()
        if len(fields) >= 2 and len(fields[0]) == 6:
            names.add(fields[1])
    return names
