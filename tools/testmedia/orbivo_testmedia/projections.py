"""Test images in the other projections of F10: equirect 180 (VR180), cubemap, EAC and fisheye.

Every eye is resampled from an equirect 360 master of the same scene (cube faces, grid, labels, depth plate).
For each target pixel the direction is computed with the INVERSE of the player's projection math
(ProjectionMath.cs / projection.hlsl compute direction -> pixel), so these images check the player independently.
VR180 needs no resampling: it is the middle half of an equirect 360 master.

Cube layouts use the notation of ffmpeg's v360 filter: faces per cell row by row (r l u d f b) and clockwise
quarter turns per cell. Unturned faces look as in faces.py (up face: top edge towards the back).
"""

from __future__ import annotations

import math
from dataclasses import dataclass

import numpy as np

from .conventions import direction_vector
from .equirect import RESOLUTION_PATCH_LAT_DEG, render_eye, resolution_patch
from .faces import FACES

FACE_BY_LETTER = {"r": "right", "l": "left", "u": "up", "d": "down", "f": "front", "b": "back"}


@dataclass(frozen=True)
class CubeLayout:
    columns: int
    rows: int
    face_order: str  # one letter per cell, row by row
    face_rotation: str  # clockwise quarter turns per cell
    padding_px: int = 0

    @property
    def preset(self) -> str | None:
        for name, layout in CUBE_PRESETS.items():
            if (layout.columns, layout.rows, layout.face_order, layout.face_rotation) == (
                    self.columns, self.rows, self.face_order, self.face_rotation):
                return name
        return None


CUBE_PRESETS = {
    "3x2": CubeLayout(3, 2, "rludfb", "000000"),
    "6x1": CubeLayout(6, 1, "rludfb", "000000"),
    "1x6": CubeLayout(1, 6, "rludfb", "000000"),
    "eac": CubeLayout(3, 2, "lfrdbu", "000313"),
}


@dataclass(frozen=True)
class Fisheye:
    fov_deg: float = 180.0
    center_x: float = 0.5  # 0 = left edge, 1 = right edge of the eye
    center_y: float = 0.5  # 0 = top edge, 1 = bottom edge
    radius: float = 0.5  # fraction of the eye height


@dataclass(frozen=True)
class Projection:
    key: str  # equirect360, equirect180, cubemap, eac, fisheye
    cube: CubeLayout | None = None
    fisheye: Fisheye | None = None

    @property
    def sidecar_name(self) -> str:
        return {"equirect360": "Equirect360", "equirect180": "Equirect180", "cubemap": "Cubemap",
                "eac": "Eac", "fisheye": "Fisheye"}[self.key]


def _face_vectors(letter: str) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    face = next(f for f in FACES if f.key == FACE_BY_LETTER[letter])
    return (np.array(face.forward, np.float64), np.array(face.right, np.float64), np.array(face.up, np.float64))


def cube_directions(layout: CubeLayout, eac: bool, width: int, height: int) -> tuple[np.ndarray, np.ndarray]:
    """Direction (height, width, 3) of every pixel center of a cube eye, and a validity mask (padding = invalid)."""
    cell_w = width / layout.columns
    cell_h = height / layout.rows
    xs = np.arange(width, dtype=np.float64) + 0.5
    ys = np.arange(height, dtype=np.float64) + 0.5
    col = np.minimum((xs // cell_w).astype(int), layout.columns - 1)
    row = np.minimum((ys // cell_h).astype(int), layout.rows - 1)

    # Position within the face content, centered: [-0.5, 0.5] across the face (x right, y down).
    face_w = cell_w - 2 * layout.padding_px
    face_h = cell_h - 2 * layout.padding_px
    px = ((xs - col * cell_w - layout.padding_px) / face_w - 0.5)[None, :].repeat(height, 0)
    py = ((ys - row * cell_h - layout.padding_px) / face_h - 0.5)[:, None].repeat(width, 1)
    cell = row[:, None] * layout.columns + col[None, :]
    valid = (np.abs(px) <= 0.5) & (np.abs(py) <= 0.5)

    directions = np.zeros((height, width, 3), np.float64)
    for index in range(6):
        mask = cell == index
        turns = int(layout.face_rotation[index])
        a, b = px[mask], py[mask]
        # Undo the clockwise turn of the cell: forward it maps (qx, qy) -> (-qy, qx) per quarter turn.
        if turns == 1:
            qx, qy = b, -a
        elif turns == 2:
            qx, qy = -a, -b
        elif turns == 3:
            qx, qy = -b, a
        else:
            qx, qy = a, b
        s, t = 2.0 * qx, -2.0 * qy
        if eac:
            s, t = np.tan(s * math.pi / 4.0), np.tan(t * math.pi / 4.0)
        forward, right, up = _face_vectors(layout.face_order[index])
        d = forward[None, :] + s[:, None] * right[None, :] + t[:, None] * up[None, :]
        directions[mask] = d / np.linalg.norm(d, axis=1, keepdims=True)
    return directions, valid


def fisheye_directions(fisheye: Fisheye, width: int, height: int) -> tuple[np.ndarray, np.ndarray]:
    """Direction of every pixel center of an equidistant fisheye eye, and the mask of the circle."""
    radius_u = fisheye.radius * height / width
    radius_v = fisheye.radius
    u = (np.arange(width, dtype=np.float64) + 0.5) / width
    v = (np.arange(height, dtype=np.float64) + 0.5) / height
    dx = ((u - fisheye.center_x) / radius_u)[None, :].repeat(height, 0)
    dy = (-(v - fisheye.center_y) / radius_v)[:, None].repeat(width, 1)
    r = np.hypot(dx, dy)
    theta = r * math.radians(fisheye.fov_deg / 2.0)
    with np.errstate(invalid="ignore", divide="ignore"):
        k = np.where(r > 1e-12, np.sin(theta) / r, 0.0)
    directions = np.stack([dx * k, dy * k, -np.cos(theta)], axis=-1)
    return directions, r <= 1.0


def sample_equirect(master: np.ndarray, directions: np.ndarray, valid: np.ndarray, chunk_rows: int = 256) -> np.ndarray:
    """Bilinear lookup of directions (h, w, 3) in an equirect 360 master; invalid pixels black."""
    mh, mw = master.shape[:2]
    out = np.zeros(directions.shape[:2] + (3,), np.uint8)
    for row0 in range(0, directions.shape[0], chunk_rows):
        d = directions[row0:row0 + chunk_rows]
        lon = np.arctan2(d[..., 0], -d[..., 2])
        lat = np.arcsin(np.clip(d[..., 1], -1.0, 1.0))
        px = (lon / (2 * math.pi) + 0.5) * mw - 0.5
        py = np.clip((0.5 - lat / math.pi) * mh - 0.5, 0.0, mh - 1.0)
        x0 = np.floor(px).astype(np.int64)
        y0 = np.floor(py).astype(np.int64)
        wx = (px - x0)[..., None]
        wy = (py - y0)[..., None]
        x1 = (x0 + 1) % mw
        x0 %= mw
        y1 = np.minimum(y0 + 1, mh - 1)
        value = (master[y0, x0].astype(np.float32) * ((1 - wx) * (1 - wy))
                 + master[y0, x1].astype(np.float32) * (wx * (1 - wy))
                 + master[y1, x0].astype(np.float32) * ((1 - wx) * wy)
                 + master[y1, x1].astype(np.float32) * (wx * wy))
        chunk = (value + 0.5).astype(np.uint8)
        chunk[~valid[row0:row0 + chunk_rows]] = 0
        out[row0:row0 + chunk_rows] = chunk
    return out


def eye_point(projection: Projection, yaw_deg: float, pitch_deg: float, width: int, height: int) -> tuple[float, float]:
    """Pixel position in one eye of the direction yaw/pitch (forward mapping, for overlays and checks)."""
    x, y, z = direction_vector(yaw_deg, pitch_deg)
    if projection.key in ("equirect360", "equirect180"):
        lon = math.atan2(x, -z)
        lat = math.asin(max(-1.0, min(1.0, y)))
        span = 2 * math.pi if projection.key == "equirect360" else math.pi
        return (lon / span + 0.5) * width, (0.5 - lat / math.pi) * height
    if projection.key == "fisheye":
        f = projection.fisheye
        lateral = math.hypot(x, y)
        angle = math.atan2(lateral, -z)
        scale = (angle / lateral if lateral > 1e-12 else 1.0) / math.radians(f.fov_deg / 2)
        u = f.center_x + x * scale * f.radius * height / width
        v = f.center_y - y * scale * f.radius
        return u * width, v * height
    layout = projection.cube
    a = (abs(x), abs(y), abs(z))
    if a[0] >= a[1] and a[0] >= a[2]:
        letter, s, t = ("r", z / a[0], y / a[0]) if x > 0 else ("l", -z / a[0], y / a[0])
    elif a[1] >= a[2]:
        letter, s, t = ("u", x / a[1], z / a[1]) if y > 0 else ("d", x / a[1], -z / a[1])
    else:
        letter, s, t = ("f", x / a[2], y / a[2]) if z < 0 else ("b", -x / a[2], y / a[2])
    if projection.key == "eac":
        s, t = 4 / math.pi * math.atan(s), 4 / math.pi * math.atan(t)
    index = layout.face_order.index(letter)
    turns = int(layout.face_rotation[index])
    qx, qy = 0.5 * s, -0.5 * t
    for _ in range(turns):
        qx, qy = -qy, qx
    cell_w, cell_h = width / layout.columns, height / layout.rows
    col, row = index % layout.columns, index // layout.columns
    return ((col + 0.5) * cell_w + qx * (cell_w - 2 * layout.padding_px),
            (row + 0.5) * cell_h + qy * (cell_h - 2 * layout.padding_px))


def pixels_per_degree(projection: Projection, yaw_deg: float, pitch_deg: float, width: int, height: int) -> float:
    """Local scale around a direction (vertical), for overlay sizes."""
    _, y0 = eye_point(projection, yaw_deg, pitch_deg - 0.5, width, height)
    _, y1 = eye_point(projection, yaw_deg, pitch_deg + 0.5, width, height)
    return abs(y1 - y0)


def master_size(projection: Projection, width: int, height: int) -> tuple[int, int]:
    """Equirect 360 master with at least the eye's pixel density in the picture center (rounded to 1024 px)."""
    if projection.key == "equirect180":
        return 2 * width, height
    density = pixels_per_degree(projection, 0.0, 0.0, width, height)  # px per degree at the front
    master_w = max(2048, min(16384, int(math.ceil(density * 360.0 * 1.25 / 1024.0) * 1024)))
    return master_w, master_w // 2


def render_projected_eye(projection: Projection, width: int, height: int, eye: str | None,
                         masters: list | None = None) -> np.ndarray:
    """One eye in `projection`. If `masters` is a list, the equirect master is appended to it (v360 check)."""
    master_w, master_h = master_size(projection, width, height)
    master = render_eye(master_w, master_h, eye)
    if masters is not None:
        masters.append(master)
    if projection.key == "equirect180":
        return np.ascontiguousarray(master[:, master_w // 4: master_w // 4 + width])
    if projection.key == "fisheye":
        directions, valid = fisheye_directions(projection.fisheye, width, height)
    else:
        directions, valid = cube_directions(projection.cube, projection.key == "eac", width, height)
    image = sample_equirect(master, directions, valid)
    draw_native_resolution_patch(image, projection)
    return image


def draw_native_resolution_patch(image: np.ndarray, projection: Projection) -> None:
    """The resolution patch drawn again in the eye's own pixels (resampling blurs the master's patch)."""
    height, width = image.shape[:2]
    lat_top, lat_bottom = RESOLUTION_PATCH_LAT_DEG
    _, y_top = eye_point(projection, 0.0, lat_top, width, height)
    x_center, y_bottom = eye_point(projection, 0.0, lat_bottom, width, height)
    density = pixels_per_degree(projection, 0.0, (lat_top + lat_bottom) / 2, width, height)
    rows = max(8, int(round(abs(y_bottom - y_top))))
    block = max(8, round(2.0 * density))
    patch = resolution_patch(block, rows)
    x0 = int(x_center - patch.shape[1] / 2)
    y0 = int(min(y_top, y_bottom))
    frame = max(2, block // 8)
    image[y0 - frame:y0 + rows + frame, x0 - frame:x0 + patch.shape[1] + frame] = 0
    image[y0:y0 + rows, x0:x0 + patch.shape[1]] = patch[:, :, None]
