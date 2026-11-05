"""Coordinate conventions for all Orbivo test media. Single source of truth for this tool.

Headset / world (OpenXR, identical to the player, see CLAUDE.md rule 9):
    right-handed, +X right, +Y up, -Z forward.

Yaw:   rotation about +Y (right-hand rule), so POSITIVE YAW TURNS LEFT. 0 deg = forward.
Pitch: positive = up.

Equirectangular 360 (docs/ARCHITECTURE.md, "Projektionsmathematik"):
    lon = atan2(x, -z), lat = asin(y)
    u = lon / (2 pi) + 0.5,  v = 0.5 - lat / pi
    -> image center = forward, image left = LEFT, image top = UP.  Note: lon = -yaw.

Stereo packing:
    top-bottom   = left eye in the top half
    side-by-side = left eye in the left half

Ambisonics (AmbiX = ACN channel order, SN3D normalization):
    X forward, Y left, Z up; azimuth counter-clockwise (left = +90 deg), elevation up.
    With the yaw convention above, ambisonics azimuth equals yaw.
    OpenXR -> Ambisonics axes: X_amb = -z, Y_amb = -x, Z_amb = y.
"""

from __future__ import annotations

import math
from dataclasses import dataclass


@dataclass(frozen=True)
class AudioDirection:
    key: str
    spoken: str  # German word announced in the audio test
    yaw_deg: float  # positive = left
    pitch_deg: float  # positive = up


# Order of the Ambisonics direction test. Clockwise around the listener, then up/down, then diagonals.
AUDIO_DIRECTIONS: tuple[AudioDirection, ...] = (
    AudioDirection("front", "vorne", 0.0, 0.0),
    AudioDirection("right", "rechts", -90.0, 0.0),
    AudioDirection("back", "hinten", 180.0, 0.0),
    AudioDirection("left", "links", 90.0, 0.0),
    AudioDirection("up", "oben", 0.0, 90.0),
    AudioDirection("down", "unten", 0.0, -90.0),
    AudioDirection("front_left", "vorne links", 45.0, 0.0),
    AudioDirection("front_right", "vorne rechts", -45.0, 0.0),
)


def direction_vector(yaw_deg: float, pitch_deg: float) -> tuple[float, float, float]:
    """Unit vector in OpenXR coordinates for a yaw/pitch pair."""
    yaw = math.radians(yaw_deg)
    pitch = math.radians(pitch_deg)
    return (-math.sin(yaw) * math.cos(pitch), math.sin(pitch), -math.cos(yaw) * math.cos(pitch))


def openxr_to_ambisonics_axes(x: float, y: float, z: float) -> tuple[float, float, float]:
    """OpenXR vector -> Ambisonics axes (X forward, Y left, Z up)."""
    return (-z, -x, y)


def lon_lat_deg(x: float, y: float, z: float) -> tuple[float, float]:
    """Equirect longitude/latitude in degrees for an OpenXR direction (lon positive = right)."""
    return math.degrees(math.atan2(x, -z)), math.degrees(math.asin(max(-1.0, min(1.0, y))))


def equirect_uv(lon_deg: float, lat_deg: float) -> tuple[float, float]:
    """Normalized equirect coordinates (0..1) for longitude/latitude in degrees."""
    return lon_deg / 360.0 + 0.5, 0.5 - lat_deg / 180.0


def equirect_pixel(lon_deg: float, lat_deg: float, width: int, height: int) -> tuple[float, float]:
    """Pixel position (x, y) of a longitude/latitude inside one eye image of the given size."""
    u, v = equirect_uv(lon_deg, lat_deg)
    return u * width, v * height
