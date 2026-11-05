"""Equirect 360 test images: cube faces projected to equirect, plus grid, degree labels,
a pixel-resolution patch and a stereo depth plate. See conventions.py for all sign conventions."""

from __future__ import annotations

import math
from dataclasses import dataclass

import numpy as np
from PIL import Image, ImageDraw

from .conventions import equirect_pixel
from .faces import FACES, FONT_BOLD, draw_faces, font

GRID_STEP_DEG = 15
DEPTH_PLATE_LON_DEG = 30.0  # upper right of VORNE, clear of the label
DEPTH_PLATE_LAT_DEG = 15.0
LABEL_HEIGHT_DEG = 2.0
YAW_LABEL_LAT_DEG = -37.5  # between the -30 and -45 grid lines
DEPTH_DISPARITY_DEG = 1.0  # crossed disparity: appears about 3.7 m away (IPD 64 mm)
RESOLUTION_PATCH_LAT_DEG = (-19.0, -25.0)  # below VORNE and the eye marker


@dataclass(frozen=True)
class Layout:
    key: str  # "mono", "tb", "sbs"
    description: str

    def eye_size(self, width: int, height: int) -> tuple[int, int]:
        if self.key == "tb":
            return width, height // 2
        if self.key == "sbs":
            return width // 2, height
        return width, height

    def eyes(self) -> tuple[str | None, ...]:
        return (None,) if self.key == "mono" else ("left", "right")


LAYOUTS = {
    "mono": Layout("mono", "Mono"),
    "tb": Layout("tb", "Stereo Top-Bottom (linkes Auge oben)"),
    "sbs": Layout("sbs", "Stereo Side-by-Side (linkes Auge links, je Auge halbe Breite)"),
}

# Frame sizes per layout and size class. 360 SBS at full width (16384) would exceed the decoder limit
# of 8192, so each SBS eye is squeezed to half horizontal resolution.
FRAME_SIZES = {
    ("mono", "4k"): (4096, 2048),
    ("tb", "4k"): (4096, 4096),
    ("sbs", "4k"): (4096, 2048),
    ("mono", "8k"): (8192, 4096),
    ("tb", "8k"): (8192, 8192),
    ("sbs", "8k"): (8192, 4096),
}


def face_size_for(eye_width: int, eye_height: int) -> int:
    """Face resolution so that the face is at least as dense as the equirect everywhere.
    At the face center, F/2 pixels per radian must match W/(2 pi) and H/pi."""
    needed = max(eye_width / math.pi, 2.0 * eye_height / math.pi)
    return int(math.ceil(needed / 64.0) * 64)


def project_faces(faces: np.ndarray, width: int, height: int, chunk_rows: int = 256) -> np.ndarray:
    """Projects (6, F, F, 3) cube faces to an equirect image (height, width, 3), bilinear sampling."""
    face_size = faces.shape[1]
    forward = np.array([f.forward for f in FACES], dtype=np.float32)
    right = np.array([f.right for f in FACES], dtype=np.float32)
    up = np.array([f.up for f in FACES], dtype=np.float32)

    lon = ((np.arange(width, dtype=np.float64) + 0.5) / width - 0.5) * 2.0 * math.pi
    sin_lon = np.sin(lon).astype(np.float32)
    cos_lon = np.cos(lon).astype(np.float32)

    out = np.empty((height, width, 3), dtype=np.uint8)
    for row0 in range(0, height, chunk_rows):
        row1 = min(height, row0 + chunk_rows)
        lat = (0.5 - (np.arange(row0, row1, dtype=np.float64) + 0.5) / height) * math.pi
        sin_lat = np.sin(lat).astype(np.float32)[:, None]
        cos_lat = np.cos(lat).astype(np.float32)[:, None]

        # OpenXR direction for every pixel: lon = atan2(x, -z), lat = asin(y)
        dx = sin_lon[None, :] * cos_lat
        dy = np.broadcast_to(sin_lat, dx.shape)
        dz = -cos_lon[None, :] * cos_lat
        d = np.stack([dx, dy, dz], axis=-1).reshape(-1, 3)

        dots = d @ forward.T
        face_index = np.argmax(dots, axis=1)
        denom = np.take_along_axis(dots, face_index[:, None], axis=1)[:, 0]
        s = np.einsum("ij,ij->i", d, right[face_index]) / denom
        t = np.einsum("ij,ij->i", d, up[face_index]) / denom

        px = np.clip((s + 1.0) * 0.5 * face_size - 0.5, 0.0, face_size - 1.0)
        py = np.clip((1.0 - t) * 0.5 * face_size - 0.5, 0.0, face_size - 1.0)
        x0 = np.floor(px).astype(np.int32)
        y0 = np.floor(py).astype(np.int32)
        x1 = np.minimum(x0 + 1, face_size - 1)
        y1 = np.minimum(y0 + 1, face_size - 1)
        wx = (px - x0)[:, None]
        wy = (py - y0)[:, None]

        value = (faces[face_index, y0, x0].astype(np.float32) * ((1 - wx) * (1 - wy))
                 + faces[face_index, y0, x1].astype(np.float32) * (wx * (1 - wy))
                 + faces[face_index, y1, x0].astype(np.float32) * ((1 - wx) * wy)
                 + faces[face_index, y1, x1].astype(np.float32) * (wx * wy))
        out[row0:row1] = (value + 0.5).astype(np.uint8).reshape(row1 - row0, width, 3)
    return out


def _blend(region: np.ndarray, color: tuple[int, int, int], alpha: float) -> None:
    region[...] = (region * (1.0 - alpha) + np.array(color, dtype=np.float32) * alpha + 0.5).astype(np.uint8)


def draw_grid(image: np.ndarray) -> None:
    """Longitude/latitude lines every 15 deg; 0/90/180 deg and the equator are thicker."""
    height, width = image.shape[:2]
    minor_x = max(1, round(width / 4096))
    minor_y = max(1, round(height / 2048))

    for lon in range(-180, 180, GRID_STEP_DEG):
        line_width = minor_x * (2 if lon % 90 == 0 else 1)
        x_center = (lon / 360.0 + 0.5) * width
        columns = np.arange(round(x_center - line_width / 2), round(x_center - line_width / 2) + line_width) % width
        _blend(image[:, columns], (255, 255, 255), 0.6)

    for lat in range(-90 + GRID_STEP_DEG, 90, GRID_STEP_DEG):
        line_width = minor_y * (2 if lat == 0 else 1)
        y_center = (0.5 - lat / 180.0) * height
        y_start = round(y_center - line_width / 2)
        _blend(image[y_start:y_start + line_width], (255, 255, 255), 0.6)


def _paste_text(canvas: Image.Image, text: str, center: tuple[float, float], height_px: float,
                x_scale: float, fill=(255, 255, 255)) -> None:
    """Draws text centered at `center`, compressed horizontally by `x_scale` (pre-compensates the
    equirect stretch away from the equator and the squeeze of side-by-side eyes)."""
    size = max(8, int(height_px))
    text_font = font(FONT_BOLD, size)
    left, top, right, bottom = text_font.getbbox(text, stroke_width=max(1, size // 12))
    tile = Image.new("RGBA", (right - left + 4, bottom - top + 4), (0, 0, 0, 0))
    ImageDraw.Draw(tile).text((2 - left, 2 - top), text, font=text_font, fill=fill,
                              stroke_width=max(1, size // 12), stroke_fill=(0, 0, 0))
    new_width = max(1, round(tile.width * x_scale))
    tile = tile.resize((new_width, tile.height), Image.LANCZOS)
    canvas.alpha_composite(tile, (round(center[0] - tile.width / 2), round(center[1] - tile.height / 2)))


def draw_degree_labels(image: np.ndarray) -> np.ndarray:
    """Yaw labels (positive = left) along latitude -30 deg, pitch labels along the front meridian."""
    height, width = image.shape[:2]
    px_per_deg_y = height / 180.0
    squeeze = (width / 360.0) / px_per_deg_y  # 1.0 for 2:1 eyes, 0.5 for squeezed SBS eyes
    canvas = Image.fromarray(image).convert("RGBA")

    label_lat = YAW_LABEL_LAT_DEG
    for lon in range(-180, 180, GRID_STEP_DEG):
        yaw = -lon
        if lon == -180:
            text = "180°"
        elif yaw == 0:
            text = "0°"
        else:
            text = f"{yaw:+d}°"
        x, y = equirect_pixel(lon, label_lat, width, height)
        _paste_text(canvas, text, (x, y), LABEL_HEIGHT_DEG * px_per_deg_y,
                    math.cos(math.radians(label_lat)) * squeeze)

    for lat in range(-75, 90, GRID_STEP_DEG):
        if lat == 0:
            continue
        cos_lat = math.cos(math.radians(lat))
        x, y = equirect_pixel(3.0 / cos_lat, lat + 1.5, width, height)
        _paste_text(canvas, f"{lat:+d}°", (x, y), LABEL_HEIGHT_DEG * px_per_deg_y, cos_lat * squeeze)

    return np.asarray(canvas.convert("RGB")).copy()


def draw_resolution_patch(image: np.ndarray) -> None:
    """Black/white stripes with 1, 2 and 4 pixel period halves plus a 1-pixel checkerboard, drawn in
    source pixels below VORNE. If the player keeps the full resolution, the 1-pixel stripes stay visible."""
    height, width = image.shape[:2]
    lat_top, lat_bottom = RESOLUTION_PATCH_LAT_DEG
    _, y0 = equirect_pixel(0.0, lat_top, width, height)
    _, y1 = equirect_pixel(0.0, lat_bottom, width, height)
    y0, y1 = int(y0), int(y1)
    block = max(8, round(2.0 * width / 360.0))  # about 2 deg per block
    patterns = []
    yy, xx = np.mgrid[0:y1 - y0, 0:block]
    for period in (1, 2, 4):
        patterns.append((xx // period) % 2)  # vertical stripes
        patterns.append((yy // period) % 2)  # horizontal stripes
    patterns.append((xx + yy) % 2)  # checkerboard
    patch = np.concatenate(patterns, axis=1).astype(np.uint8) * 255
    x0 = int(width / 2 - patch.shape[1] / 2)
    frame = max(1, block // 16)
    image[y0 - frame:y1 + frame, x0 - frame:x0 + patch.shape[1] + frame] = 0
    image[y0:y1, x0:x0 + patch.shape[1]] = patch[:, :, None]


def draw_depth_plate(image: np.ndarray, eye: str | None) -> None:
    """A plate with crossed disparity: in front of the background when the eyes are correct,
    behind it (hard to fuse) when they are swapped."""
    height, width = image.shape[:2]
    offset = {None: 0.0, "left": DEPTH_DISPARITY_DEG / 2, "right": -DEPTH_DISPARITY_DEG / 2}[eye]
    plate_w_deg, plate_h_deg = 12.0, 5.0
    plate_w = round(plate_w_deg * width / 360.0)
    plate_h = round(plate_h_deg * height / 180.0)
    plate = Image.new("RGB", (plate_w, plate_h), (250, 250, 250))
    draw = ImageDraw.Draw(plate)
    border = max(2, plate_h // 16)
    draw.rectangle([0, 0, plate_w - 1, plate_h - 1], outline=(20, 20, 20), width=border)
    squeeze = (width / 360.0) / (height / 180.0)
    text_font = font(FONT_BOLD, int(plate_h * 0.5))
    tile = Image.new("RGB", (int(plate_w / squeeze), plate_h), (250, 250, 250))
    ImageDraw.Draw(tile).text((tile.width / 2, plate_h / 2), "TIEFE", font=text_font, fill=(20, 20, 20), anchor="mm")
    tile = tile.resize((plate_w - 2 * border, plate_h - 2 * border), Image.LANCZOS)
    plate.paste(tile, (border, border))
    x, y = equirect_pixel(DEPTH_PLATE_LON_DEG + offset, DEPTH_PLATE_LAT_DEG, width, height)
    x0, y0 = round(x - plate_w / 2), round(y - plate_h / 2)
    image[y0:y0 + plate_h, x0:x0 + plate_w] = np.asarray(plate)


def render_eye(eye_width: int, eye_height: int, eye: str | None) -> np.ndarray:
    faces = draw_faces(face_size_for(eye_width, eye_height), eye)
    image = project_faces(faces, eye_width, eye_height)
    draw_grid(image)
    image = draw_degree_labels(image)
    draw_resolution_patch(image)
    draw_depth_plate(image, eye)
    return image


def render_frame(layout: Layout, width: int, height: int) -> np.ndarray:
    """Full frame (height, width, 3) for a layout: mono, top-bottom or side-by-side."""
    eye_width, eye_height = layout.eye_size(width, height)
    eyes = [render_eye(eye_width, eye_height, eye) for eye in layout.eyes()]
    if layout.key == "tb":
        return np.concatenate(eyes, axis=0)
    if layout.key == "sbs":
        return np.concatenate(eyes, axis=1)
    return eyes[0]


def eye_regions(layout: Layout, width: int, height: int) -> list[tuple[int, int, int, int]]:
    """(x, y, w, h) of each eye inside the frame, left eye first."""
    eye_width, eye_height = layout.eye_size(width, height)
    if layout.key == "tb":
        return [(0, 0, eye_width, eye_height), (0, eye_height, eye_width, eye_height)]
    if layout.key == "sbs":
        return [(0, 0, eye_width, eye_height), (eye_width, 0, eye_width, eye_height)]
    return [(0, 0, width, height)]
