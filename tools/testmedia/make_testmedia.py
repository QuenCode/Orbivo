"""Generates Orbivo test media: equirect 360 test images and videos (mono, top-bottom, side-by-side;
4K and 8K), the other projections (VR180, cubemap, EAC, fisheye) with Spherical Video V2 metadata and
sidecar files, and a first-order Ambisonics direction test (AmbiX and FuMa). See README.md."""

from __future__ import annotations

import argparse
import json
import platform
import shutil
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
from PIL import Image

from orbivo_testmedia import ambisonics, conventions, equirect, projections, speech, spherical, variants, video
from orbivo_testmedia.wavfile import write_wav

TOOL_DIR = Path(__file__).resolve().parent
REPO_ROOT = TOOL_DIR.parents[1]
DEFAULT_FFMPEG = REPO_ROOT / "native" / "win-x64" / "ffmpeg.exe"
DEFAULT_OUT = TOOL_DIR / "out"
DEFAULT_DURATION_S = 20.0
LONG_DURATION_S = 600.0
V360_LIMIT = 6.0  # mean absolute difference (0-255) to ffmpeg's v360 that still counts as the same picture


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT, help="output directory (default: %(default)s)")
    parser.add_argument("--ffmpeg", type=Path, default=DEFAULT_FFMPEG, help="ffmpeg.exe (default: %(default)s)")
    parser.add_argument("--what", default="images,videos,audio", help="images, videos, audio (comma separated)")
    parser.add_argument("--sizes", default="4k,8k", help="4k, 8k (comma separated)")
    parser.add_argument("--layouts", default="mono,tb,sbs", help="equirect 360 only: mono, tb, sbs (comma separated)")
    parser.add_argument("--formats", default=",".join(variants.PROJECTION_KEYS),
                        help="projections: " + ", ".join(variants.PROJECTION_KEYS) + " (comma separated)")
    parser.add_argument("--codec", default="hevc_nvenc", choices=sorted(video.CODECS), help="video encoder")
    parser.add_argument("--bit-depth", type=int, default=8, choices=(8, 10))
    parser.add_argument("--fps", type=int, default=30)
    parser.add_argument("--duration", type=float, default=20.0, help="video length in seconds")
    parser.add_argument("--long", action="store_true",
                        help=f"only the {LONG_DURATION_S:.0f} s 4K mono video for the A/V long test (M5)")
    args = parser.parse_args()
    if args.long:
        args.what, args.sizes, args.layouts, args.duration = "videos", "4k", "mono", LONG_DURATION_S
        args.formats = "equirect360"
    return args


def split(value: str, allowed: set[str], name: str) -> list[str]:
    items = [item.strip().lower() for item in value.split(",") if item.strip()]
    unknown = [item for item in items if item not in allowed]
    if unknown:
        sys.exit(f"Unknown {name}: {', '.join(unknown)} (allowed: {', '.join(sorted(allowed))})")
    return items


def log(message: str) -> None:
    print(f"[{time.strftime('%H:%M:%S')}] {message}", flush=True)


def video_settings(args, what) -> video.VideoSettings | None:
    if "videos" not in what:
        return None
    if not args.ffmpeg.exists():
        sys.exit(f"ffmpeg not found: {args.ffmpeg} (see native/README.md or pass --ffmpeg)")
    codec = video.CODECS[args.codec]
    if codec.name not in video.available_encoders(args.ffmpeg):
        sys.exit(f"Encoder {codec.name} is not available in {args.ffmpeg}")
    if args.bit_depth == 10 and codec.pix_fmt_10 is None:
        sys.exit(f"{codec.name} does not support 10 bit")
    return video.VideoSettings(codec, args.bit_depth, args.fps, args.duration)


def video_name(stem: str, args, settings: video.VideoSettings) -> str:
    # Other lengths than the default get their own name, so the long test video sits next to the short one.
    length = "" if args.duration == DEFAULT_DURATION_S else f"_{args.duration:g}s"
    return f"{stem}_{settings.codec.short}_{args.bit_depth}bit_{args.fps}fps{length}.mp4"


def video_entry(name: str, settings: video.VideoSettings, args, **fields) -> dict:
    return {
        "file": name, **fields,
        "encoder": settings.codec.name, "pix_fmt": settings.pix_fmt, "fps": args.fps,
        "duration_s": args.duration, "gop_frames": args.fps,
        "color": "BT.709, limited range",
        "audio": f"stereo AAC, {video.BEEP_HZ} Hz beep for {settings.flash_s} s at every full second",
    }


def tag(path: Path, metadata: spherical.SphericalMetadata | None) -> None:
    if metadata is not None:
        spherical.inject(path, metadata)


def write_sidecar(video_path: Path, document: dict | None) -> str | None:
    """Writes <video>.orbivo.json, or removes an old one if the variant has none."""
    sidecar = video_path.with_name(video_path.name + ".orbivo.json")
    if document is None:
        sidecar.unlink(missing_ok=True)
        return None
    sidecar.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    return sidecar.name


def generate_equirect360(args, what, sizes, layouts, settings, manifest) -> None:
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
                    "file": still.name, "projection": "equirect360", "layout": layout_key,
                    "description": layout.description,
                    "width": width, "height": height, "eye_width": eye_width, "eye_height": eye_height,
                })

            if settings is None:
                continue
            if max(width, height) > settings.codec.max_size:
                log(f"skip video {stem}: {settings.codec.name} supports at most {settings.codec.max_size} px")
                continue
            name = video_name(stem, args, settings)
            output = args.out / name
            elapsed = video.encode(args.ffmpeg, still, output, layout, width, height, settings)
            overlays = {
                "timecode_lat_deg": video.TIMECODE_LAT_DEG,
                "orbit_marker_lat_deg": video.ORBIT_LAT_DEG,
                "orbit_period_s": args.duration,
                "sync_flash_lat_deg": video.FLASH_LAT_DEG,
            }
            base = dict(projection="equirect360", layout=layout_key, description=layout.description,
                        width=width, height=height, eye_width=eye_width, eye_height=eye_height, overlays=overlays)

            # Without st3d the player guesses from the size (square = top-bottom): one copy without any metadata,
            # one with sv3d but no st3d (incomplete metadata, the player warns).
            if layout_key == "tb" and size == "4k" and args.duration == DEFAULT_DURATION_S:
                for suffix, extra in (("_nometa", None), ("_sv3d_only", spherical.SphericalMetadata(None, "equirect"))):
                    copy = output.with_name(output.stem + suffix + output.suffix)
                    shutil.copyfile(output, copy)
                    tag(copy, extra)
                    write_sidecar(copy, None)
                    described = extra.describe() if extra else "none"
                    manifest["videos"].append(video_entry(copy.name, settings, args, **base, metadata=described))
                    log(f"{copy.name} (metadata: {described})")

            metadata = spherical.SphericalMetadata(layout_key, "equirect")
            tag(output, metadata)
            write_sidecar(output, None)
            log(f"{name} ({elapsed:.0f} s, metadata: {metadata.describe()})")
            manifest["videos"].append(video_entry(name, settings, args, **base, metadata=metadata.describe()))
            if "images" not in what and not still_existed:
                still.unlink()


def render_variant_frame(variant: variants.Variant, width: int, height: int,
                         masters: list | None = None) -> np.ndarray:
    layout = equirect.LAYOUTS[variant.layout]
    eye_width, eye_height = layout.eye_size(width, height)
    eyes = [projections.render_projected_eye(variant.projection, eye_width, eye_height, eye, masters)
            for eye in layout.eyes()]
    if variant.layout == "tb":
        return np.concatenate(eyes, axis=0)
    if variant.layout == "sbs":
        return np.concatenate(eyes, axis=1)
    return eyes[0]


def v360_difference(args, master: np.ndarray, output_format: str, ours: np.ndarray, variant: variants.Variant) -> float:
    """Mean absolute difference (0-255) between our image and ffmpeg's v360 conversion of the same master,
    over the pixels both show. An independent check of face order, face turns and the fisheye mapping."""
    work = args.out / "_work"
    work.mkdir(exist_ok=True)
    master_path, v360_path = work / "v360_master.png", work / "v360_result.png"
    Image.fromarray(master).save(master_path, compress_level=1)
    height, width = ours.shape[:2]
    options = f"input=e:output={output_format}:w={width}:h={height}"
    if output_format == "fisheye":
        fov = variant.projection.fisheye.fov_deg
        options += f":h_fov={fov}:v_fov={fov}"
    subprocess.run([str(args.ffmpeg), "-hide_banner", "-loglevel", "error", "-y", "-i", str(master_path),
                    "-vf", f"v360={options}", str(v360_path)], check=True)
    theirs = np.asarray(Image.open(v360_path).convert("RGB")).astype(np.int16)
    mask = (ours.max(axis=2) > 0) & (theirs.max(axis=2) > 0)
    return float(np.abs(theirs - ours.astype(np.int16))[mask].mean())


def generate_variants(args, what, sizes, formats, settings, manifest) -> None:
    for variant in variants.VARIANTS:
        if variant.projection.key not in formats:
            continue
        for size in sizes:
            if size not in variant.sizes:
                continue
            width, height = variant.sizes[size]
            layout = equirect.LAYOUTS[variant.layout]
            eye_width, eye_height = layout.eye_size(width, height)
            stem = f"{variant.name}_{width}x{height}"
            still = args.out / f"{stem}.png"
            still_existed = still.exists()

            started = time.monotonic()
            masters: list = []
            frame = render_variant_frame(variant, width, height, masters)
            Image.fromarray(frame).save(still, compress_level=3)
            check = None
            if variant.v360 and variant.layout == "mono" and args.ffmpeg.exists():
                check = round(v360_difference(args, masters[0], variant.v360, frame, variant), 2)
            del masters
            log(f"{still.name} ({time.monotonic() - started:.0f} s"
                + (f", v360 '{variant.v360}' mean difference {check}" if check is not None else "") + ")")
            if check is not None and check > V360_LIMIT:
                sys.exit(f"{still.name} differs from ffmpeg v360 by {check} on average (limit {V360_LIMIT}).")

            projection = variant.projection
            cube = projection.cube
            fields = {
                "projection": projection.key, "layout": variant.layout, "description": variant.description,
                "width": width, "height": height, "eye_width": eye_width, "eye_height": eye_height,
                "cubemap_layout": None if cube is None else {
                    "preset": cube.preset, "columns": cube.columns, "rows": cube.rows,
                    "face_order": cube.face_order, "face_rotation": cube.face_rotation},
                "fisheye": None if projection.fisheye is None else variants.fisheye_fields(projection.fisheye),
            }
            if "images" in what:
                manifest["images"].append({"file": still.name, **fields, "v360_mean_difference": check})

            if settings is None:
                continue
            name = video_name(stem, args, settings)
            output = args.out / name
            elapsed = video.encode(args.ffmpeg, still, output, layout, width, height, settings, projection)
            tag(output, variant.metadata)
            document = variants.sidecar_document(variant)
            sidecar = write_sidecar(output, document)
            metadata = variant.metadata.describe() if variant.metadata else "none"
            log(f"{name} ({elapsed:.0f} s, metadata: {metadata}" + (f", sidecar {sidecar}" if sidecar else "") + ")")
            manifest["videos"].append(video_entry(
                name, settings, args, **fields, metadata=metadata, sidecar=document,
                overlays={"timecode_lat_deg": video.TIMECODE_LAT_DEG, "sync_flash_lat_deg": video.FLASH_LAT_DEG,
                          "orbit_marker": projection.key in ("equirect360", "equirect180")}))
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
    formats = split(args.formats, set(variants.PROJECTION_KEYS), "--formats")
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
        settings = video_settings(args, what)
        if "equirect360" in formats:
            generate_equirect360(args, what, sizes, layouts, settings, manifest)
        generate_variants(args, what, sizes, formats, settings, manifest)
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
