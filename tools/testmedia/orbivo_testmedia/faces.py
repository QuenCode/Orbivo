"""Draws the six cube faces of the orientation test scene.

Each face is drawn as seen from inside the cube, upright for a viewer who faces forward and then
turns (or tilts the head) towards that face. The faces are later projected to equirect, so the
labels look undistorted in the headset, including OBEN and UNTEN at the poles.
"""

from __future__ import annotations

from dataclasses import dataclass
from functools import lru_cache
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

FONT_DIR = Path(r"C:\Windows\Fonts")
FONT_BOLD = FONT_DIR / "segoeuib.ttf"
FONT_MONO = FONT_DIR / "consola.ttf"


@dataclass(frozen=True)
class Face:
    key: str
    label: str
    color: tuple[int, int, int]
    forward: tuple[float, float, float]  # OpenXR direction of the face center
    right: tuple[float, float, float]  # image +x on the face
    up: tuple[float, float, float]  # image -y on the face
    hint: str | None  # where VORNE is, seen from this face
    hint_t: float  # vertical position of the hint in face coordinates (-1 bottom .. +1 top)
    eye_t: float = -0.2  # vertical position of the eye marker (stereo only)


# right = forward x up (right-handed). Checked: front/back/left/right keep +Y as up;
# looking up from the forward pose, the top of the view points backwards (+Z);
# looking down, it points forwards (-Z).
FACES: tuple[Face, ...] = (
    # Front: eye marker above the label; below it the video shows timecode and resolution patch.
    Face("front", "VORNE", (46, 125, 50), (0, 0, -1), (1, 0, 0), (0, 1, 0), None, 0.0, eye_t=0.22),
    Face("back", "HINTEN", (183, 28, 28), (0, 0, 1), (-1, 0, 0), (0, 1, 0), "VORNE ist hinter dir", 0.25),
    Face("left", "LINKS", (21, 101, 192), (-1, 0, 0), (0, 0, -1), (0, 1, 0), "VORNE \u2192", 0.25),
    Face("right", "RECHTS", (230, 100, 0), (1, 0, 0), (0, 0, 1), (0, 1, 0), "\u2190 VORNE", 0.25),
    Face("up", "OBEN", (0, 121, 133), (0, 1, 0), (1, 0, 0), (0, 0, 1), "\u2193 VORNE", -0.55),
    Face("down", "UNTEN", (106, 27, 154), (0, -1, 0), (1, 0, 0), (0, 0, -1), "\u2191 VORNE", 0.55),
)

EYE_TEXT = {"left": "LINKES AUGE", "right": "RECHTES AUGE"}


@lru_cache(maxsize=32)
def font(path: Path, size: int) -> ImageFont.FreeTypeFont:
    return ImageFont.truetype(str(path), size)


def _t_to_y(t: float, size: int) -> float:
    return (1.0 - t) * 0.5 * size


def draw_face(face: Face, size: int, eye: str | None) -> Image.Image:
    """One face image (RGB). `eye` is None for mono, otherwise "left" or "right"."""
    image = Image.new("RGB", (size, size), face.color)
    draw = ImageDraw.Draw(image)
    center_x = size / 2

    draw.text((center_x, _t_to_y(0.0, size)), face.label, font=font(FONT_BOLD, int(size * 0.16)),
              fill="white", anchor="mm")

    if face.hint:
        draw.text((center_x, _t_to_y(face.hint_t, size)), face.hint, font=font(FONT_BOLD, int(size * 0.06)),
                  fill="white", anchor="mm")

    if eye is not None:
        draw.text((center_x, _t_to_y(face.eye_t, size)), EYE_TEXT[eye], font=font(FONT_BOLD, int(size * 0.06)),
                  fill=(255, 255, 160), anchor="mm")

    return image


def draw_faces(size: int, eye: str | None) -> np.ndarray:
    """All six faces as one array (6, size, size, 3) in the order of FACES."""
    return np.stack([np.asarray(draw_face(face, size, eye)) for face in FACES])
