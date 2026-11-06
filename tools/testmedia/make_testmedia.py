"""Generates Orbivo test media: equirect 360 test images and videos (mono, top-bottom, side-by-side;
4K and 8K) and a first-order Ambisonics direction test (AmbiX and FuMa). See README.md."""

from __future__ import annotations

import argparse
import json
import platform
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
from PIL import Image

from orbivo_testmedia import ambisonics, conventions, equirect, speech, video
from orbivo_testmedia.wavfile import write_wav

TOOL_DIR = Path(__file__).resolve().parent
REPO_ROOT = TOOL_DIR.parents[1]
DEFAULT_FFMPEG = REPO_ROOT / "native" / "win-x64" / "ffmpeg.exe"
DEFAULT_OUT = TOOL_DIR / "out"
DEFAULT_DURATION_S = 20.0
LONG_DURATION_S = 600.0


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT, help="output directory (default: %(default)s)")
    parser.add_argument("--ffmpeg", type=Path, default=DEFAULT_FFMPEG, help="ffmpeg.exe (default: %(default)s)")
    parser.add_argument("--what", default="images,videos,audio", help="images, videos, audio (comma separated)")
    parser.add_argument("--sizes", default="4k,8k", help="4k, 8k (comma separated)")
    parser.add_argument("--layouts", default="mono,tb,sbs", help="mono, tb, sbs (comma separated)")
    parser.add_argument("--codec", default="hevc_nvenc", choices=sorted(video.CODECS), help="video encoder")
    parser.add_argument("--bit-depth", type=int, default=8, choices=(8, 10))
    parser.add_argument("--fps", type=int, default=30)
    parser.add_argument("--duration", type=float, default=20.0, help="video length in seconds")
    parser.add_argument("--long", action="store_true",
                        help=f"only the {LONG_DURATION_S:.0f} s 4K mono video for the A/V long test (M5)")
    args = parser.parse_args()
    if args.long:
        args.what, args.sizes, args.layouts, args.duration = "videos", "4k", "mono", LONG_DURATION_S
    return args


def split(value: str, allowed: set[str], name: str) -> list[str]:
    items = [item.strip().lower() for item in value.split(",") if item.strip()]
    unknown = [item for item in items if item not in allowed]
    if unknown:
        sys.exit(f"Unknown {name}: {', '.join(unknown)} (allowed: {', '.join(sorted(allowed))})")
    return items


def log(message: str) -> None:
    print(f"[{time.strftime('%H:%M:%S')}] {message}", flush=True)


def generate_visuals(args, what, sizes, layouts, manifest) -> None:
    settings = None
    if "videos" in what:
        if not args.ffmpeg.exists():
            sys.exit(f"ffmpeg not found: {args.ffmpeg} (see native/README.md or pass --ffmpeg)")
        codec = video.CODECS[args.codec]
        if codec.name not in video.available_encoders(args.ffmpeg):
            sys.exit(f"Encoder {codec.name} is not available in {args.ffmpeg}")
        if args.bit_depth == 10 and codec.pix_fmt_10 is None:
            sys.exit(f"{codec.name} does not support 10 bit")
        settings = video.VideoSettings(codec, args.bit_depth, args.fps, args.duration)

    for size in sizes:
        for layout_key in layouts:
            layout = equirect.LAYOUTS[layout_key]
            width, height = equirect.FRAME_SIZES[(layout_key, size)]
            eye_width, eye_height = layout.eye_size(width, height)
            stem = f"equirect360_{layout_key}_{width}x{height}"
            still = args.out / f"{stem}.png"

            still_existed = still.exists()
            started = time.monotonic()
            frame = equirect.render_frame(layout, width, height)
            Image.fromarray(frame).save(still, compress_level=3)
            log(f"{still.name} ({time.monotonic() - started:.0f} s)")
            if "images" in what:
                manifest["images"].append({
                    "file": still.name, "layout": layout_key, "description": layout.description,
                    "width": width, "height": height, "eye_width": eye_width, "eye_height": eye_height,
                })

            if settings is None:
                continue
            if max(width, height) > settings.codec.max_size:
                log(f"skip video {stem}: {settings.codec.name} supports at most {settings.codec.max_size} px")
                continue
            # Other lengths than the default get their own name, so the long test video sits next to the short one.
            length = "" if args.duration == DEFAULT_DURATION_S else f"_{args.duration:g}s"
            name = f"{stem}_{settings.codec.short}_{args.bit_depth}bit_{args.fps}fps{length}.mp4"
            elapsed = video.encode(args.ffmpeg, still, args.out / name, layout, width, height, settings)
            log(f"{name} ({elapsed:.0f} s)")
            manifest["videos"].append({
                "file": name, "layout": layout_key, "description": layout.description,
                "width": width, "height": height, "eye_width": eye_width, "eye_height": eye_height,
                "encoder": settings.codec.name, "pix_fmt": settings.pix_fmt, "fps": args.fps,
                "duration_s": args.duration, "gop_frames": args.fps,
                "color": "BT.709, limited range",
                "audio": f"stereo AAC, {video.BEEP_HZ} Hz beep for {settings.flash_s} s at every full second",
                "overlays": {
                    "timecode_lat_deg": video.TIMECODE_LAT_DEG,
                    "orbit_marker_lat_deg": video.ORBIT_LAT_DEG,
                    "orbit_period_s": args.duration,
                    "sync_flash_lat_deg": video.FLASH_LAT_DEG,
                },
            })
            if "images" not in what and not still_existed:
                still.unlink()


def generate_audio(args, manifest) -> None:
    words = {direction.key: direction.spoken for direction in conventions.AUDIO_DIRECTIONS}
    signals, voice = speech.synthesize(words, args.out / "_work", ambisonics.SAMPLE_RATE)
    if signals is None:
        log(f"WARNING: no German voice ({voice}); the audio test contains noise bursts only")
    else:
        log(f"speech voice: {voice}")

    ambix, events = ambisonics.build_direction_test(signals)
    files = {
        "ambix_foa_richtungstest.wav": ("AmbiX (ACN/SN3D), channels W Y Z X", ambix),
        "fuma_foa_richtungstest.wav": ("FuMa (MaxN), channels W X Y Z, W -3 dB", ambisonics.ambix_to_fuma(ambix)),
    }
    for name, (format_description, samples) in files.items():
        write_wav(args.out / name, samples, ambisonics.SAMPLE_RATE, bits=24)
        log(f"{name} ({samples.shape[1] / ambisonics.SAMPLE_RATE:.1f} s, peak {np.max(np.abs(samples)):.2f})")
        manifest["audio"].append({
            "file": name, "format": format_description, "order": 1, "channels": 4,
            "sample_rate": ambisonics.SAMPLE_RATE, "bits": 24, "wav_channel_mask": 0,
            "speech_voice": voice if signals is not None else None,
            "events": events,
        })


def main() -> None:
    args = parse_args()
    what = split(args.what, {"images", "videos", "audio"}, "--what")
    sizes = split(args.sizes, {"4k", "8k"}, "--sizes")
    layouts = split(args.layouts, set(equirect.LAYOUTS), "--layouts")
    args.out.mkdir(parents=True, exist_ok=True)

    manifest = {
        "generator": "tools/testmedia/make_testmedia.py",
        "created_utc": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "python": platform.python_version(),
        "conventions": conventions.__doc__.strip(),
        "images": [], "videos": [], "audio": [],
    }
    manifest_path = args.out / "manifest.json"

    started = time.monotonic()
    if "images" in what or "videos" in what:
        generate_visuals(args, what, sizes, layouts, manifest)
    if "audio" in what:
        generate_audio(args, manifest)

    # Keep the entries of files that were not generated this time (e.g. the short videos when only --long ran).
    if manifest_path.exists():
        previous = json.loads(manifest_path.read_text(encoding="utf-8"))
        for kind in ("images", "videos", "audio"):
            fresh = {entry["file"] for entry in manifest[kind]}
            kept = [entry for entry in previous.get(kind, []) if entry["file"] not in fresh and (args.out / entry["file"]).exists()]
            manifest[kind] = kept + manifest[kind]

    manifest_path.write_text(json.dumps(manifest, indent=2, ensure_ascii=False), encoding="utf-8")
    log(f"done in {time.monotonic() - started:.0f} s -> {args.out}")


if __name__ == "__main__":
    main()
