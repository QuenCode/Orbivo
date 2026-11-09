"""Writes Spherical Video V2 metadata (st3d, sv3d) and Spatial Audio metadata (SA3D) into an MP4 file, with the Python
standard library only.

ffmpeg reads these boxes (side data "Spherical Mapping" / "Stereo 3D"), but its command line cannot attach them
to a newly encoded stream. So the boxes are added afterwards to the video track's sample entry
(moov/trak/mdia/minf/stbl/stsd/<hvc1|avc1|av01>); sizes are fixed up and, if moov lies before mdat,
the chunk offsets (stco/co64) are shifted by the number of added bytes.

Box layout (https://github.com/google/spatial-media/blob/master/docs/spherical-video-v2-rfc.md):
  st3d  FullBox: stereo_mode u8 (0 mono, 1 top-bottom, 2 left-right)
  sv3d  Box: svhd FullBox (metadata_source, null-terminated)
             proj Box: prhd FullBox (yaw, pitch, roll: 16.16 fixed)
                       equi FullBox (bounds top, bottom, left, right: 0.32 fixed) | cbmp FullBox (layout, padding)
"""

from __future__ import annotations

import os
import struct
from dataclasses import dataclass
from pathlib import Path

CONTAINERS = {b"moov", b"trak", b"mdia", b"minf", b"stbl"}
VISUAL_SAMPLE_ENTRY_HEADER = 8 + 78  # box header + VisualSampleEntry fields before the child boxes
AUDIO_SAMPLE_ENTRY_HEADER = 8 + 28  # box header + AudioSampleEntry (version 0) fields before the child boxes
STEREO_MODES = {"mono": 0, "tb": 1, "sbs": 2}


@dataclass(frozen=True)
class SphericalMetadata:
    stereo: str | None  # "mono", "tb", "sbs" or None (no st3d)
    projection: str | None  # "equirect", "equirect180", "cubemap" or None (no sv3d)
    cubemap_padding: int = 0

    def describe(self) -> str:
        parts = []
        if self.projection:
            parts.append(f"sv3d {self.projection}")
        if self.stereo:
            parts.append(f"st3d {self.stereo}")
        return ", ".join(parts) or "none"


def _box(kind: bytes, payload: bytes) -> bytes:
    return struct.pack(">I", 8 + len(payload)) + kind + payload


def _full_box(kind: bytes, payload: bytes, version: int = 0, flags: int = 0) -> bytes:
    return _box(kind, struct.pack(">I", (version << 24) | flags) + payload)


def build_boxes(metadata: SphericalMetadata) -> bytes:
    data = b""
    if metadata.stereo is not None:
        data += _full_box(b"st3d", struct.pack(">B", STEREO_MODES[metadata.stereo]))
    if metadata.projection is not None:
        header = _full_box(b"svhd", b"Orbivo testmedia\0")
        pose = _full_box(b"prhd", struct.pack(">iii", 0, 0, 0))
        if metadata.projection == "cubemap":
            projection = _full_box(b"cbmp", struct.pack(">II", 0, metadata.cubemap_padding))
        else:
            # VR180: the picture covers the middle half in longitude; bounds cut a quarter on each side.
            side = 0x40000000 if metadata.projection == "equirect180" else 0
            projection = _full_box(b"equi", struct.pack(">IIII", 0, 0, side, side))
        data += _box(b"sv3d", header + _box(b"proj", pose + projection))
    return data


def _children(data: bytes, start: int, end: int):
    """(offset, size, kind, header size) of the boxes in data[start:end]."""
    offset = start
    while offset + 8 <= end:
        size, kind = struct.unpack(">I4s", data[offset:offset + 8])
        header = 8
        if size == 1:
            size = struct.unpack(">Q", data[offset + 8:offset + 16])[0]
            header = 16
        elif size == 0:
            size = end - offset
        if size < header or offset + size > end:
            raise ValueError(f"broken box {kind!r} at {offset}")
        yield offset, size, kind, header
        offset += size


@dataclass(frozen=True)
class SpatialAudioMetadata:
    order: int = 1
    channel_map: tuple[int, ...] | None = None  # None = identity

    def build(self) -> bytes:
        channels = (self.order + 1) ** 2
        mapping = self.channel_map if self.channel_map is not None else tuple(range(channels))
        if len(mapping) != channels:
            raise ValueError("channel map must list (order + 1)^2 channels")
        payload = struct.pack(">BBIBBI", 0, 0, self.order, 0, 0, channels) + b"".join(struct.pack(">I", c) for c in mapping)
        return _box(b"SA3D", payload)

    def describe(self) -> str:
        return f"SA3D order {self.order}"


def _find_sample_entry(data: bytes, moov: tuple[int, int, int], handler_type: bytes = b"vide") -> list[tuple[int, int]]:
    """Path [(offset, header size), ...] from moov down to the first sample entry of a `handler_type` track."""
    moov_offset, moov_size, moov_header = moov
    for trak_offset, trak_size, kind, trak_header in _children(data, moov_offset + moov_header, moov_offset + moov_size):
        if kind != b"trak":
            continue
        path = [(moov_offset, moov_header), (trak_offset, trak_header)]
        node = (trak_offset, trak_size, trak_header)
        handler = None
        for name in (b"mdia", b"minf", b"stbl"):
            found = None
            for offset, size, kind, header in _children(data, node[0] + node[2], node[0] + node[1]):
                if kind == b"hdlr":
                    handler = data[offset + header + 8:offset + header + 12]
                if kind == name:
                    found = (offset, size, header)
            if found is None:
                break
            path.append((found[0], found[2]))
            node = found
        else:
            if handler != handler_type:
                continue
            for offset, size, kind, header in _children(data, node[0] + node[2], node[0] + node[1]):
                if kind == b"stsd":
                    entry = offset + header + 8  # FullBox flags + entry_count
                    path.append((offset, header))
                    path.append((entry, 8))
                    return path
    raise ValueError(f"no {handler_type.decode()} track found")


def _strip_existing(entry: bytes, header: int = VISUAL_SAMPLE_ENTRY_HEADER, kinds=(b"st3d", b"sv3d")) -> bytes:
    """The sample entry without old boxes of the given kinds."""
    keep = entry[:header]
    for offset, size, kind, _ in _children(entry, header, len(entry)):
        if kind not in kinds:
            keep += entry[offset:offset + size]
    return keep


def _shift_chunk_offsets(moov: bytearray, delta: int) -> None:
    def walk(start: int, end: int) -> None:
        for offset, size, kind, header in _children(moov, start, end):
            if kind in CONTAINERS:
                walk(offset + header, offset + size)
            elif kind in (b"stco", b"co64"):
                count = struct.unpack(">I", moov[offset + header + 4:offset + header + 8])[0]
                width = 4 if kind == b"stco" else 8
                fmt = ">I" if kind == b"stco" else ">Q"
                for i in range(count):
                    position = offset + header + 8 + i * width
                    value = struct.unpack(fmt, moov[position:position + width])[0] + delta
                    if kind == b"stco" and value > 0xFFFFFFFF:
                        raise ValueError("chunk offset no longer fits into stco")
                    moov[position:position + width] = struct.pack(fmt, value)
    moov_header = 16 if struct.unpack(">I", moov[0:4])[0] == 1 else 8
    walk(moov_header, len(moov))


def inject(path: Path, metadata: SphericalMetadata) -> None:
    """Writes st3d/sv3d into the video sample entry of `path` (replacing old ones)."""
    _rewrite_sample_entry(path, b"vide", lambda entry: _strip_existing(entry) + build_boxes(metadata))


def inject_audio(path: Path, metadata: SpatialAudioMetadata) -> None:
    """Writes SA3D into the sound sample entry of `path` (replacing an old one)."""
    def rewrite(entry: bytes) -> bytes:
        version = struct.unpack(">H", entry[16:18])[0]
        if version != 0:
            raise ValueError("only version 0 sound sample entries are supported")
        return _strip_existing(entry, AUDIO_SAMPLE_ENTRY_HEADER, (b"SA3D",)) + metadata.build()
    _rewrite_sample_entry(path, b"soun", rewrite)


def _rewrite_sample_entry(path: Path, handler_type: bytes, rewrite) -> None:
    data = path.read_bytes()
    top = list(_children(data, 0, len(data)))
    moov = next(((o, s, h) for o, s, k, h in top if k == b"moov"), None)
    mdat = next((o for o, _, k, _ in top if k == b"mdat"), None)
    if moov is None or mdat is None:
        raise ValueError(f"{path.name}: moov or mdat missing")

    path_boxes = _find_sample_entry(data, moov, handler_type)
    entry_offset = path_boxes[-1][0]
    entry_size = struct.unpack(">I", data[entry_offset:entry_offset + 4])[0]
    old_entry = data[entry_offset:entry_offset + entry_size]
    new_entry = rewrite(old_entry)
    new_entry = struct.pack(">I", len(new_entry)) + new_entry[4:]
    delta = len(new_entry) - len(old_entry)

    moov_offset, moov_size, _ = moov
    new_moov = bytearray(data[moov_offset:entry_offset] + new_entry + data[entry_offset + entry_size:moov_offset + moov_size])
    for offset, header in path_boxes[:-1]:
        relative = offset - moov_offset
        if header != 8:
            raise ValueError("64-bit box sizes in moov are not supported")
        size = struct.unpack(">I", new_moov[relative:relative + 4])[0]
        new_moov[relative:relative + 4] = struct.pack(">I", size + delta)
    if moov_offset < mdat:
        _shift_chunk_offsets(new_moov, delta)

    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_bytes(data[:moov_offset] + bytes(new_moov) + data[moov_offset + moov_size:])
    os.replace(temporary, path)
