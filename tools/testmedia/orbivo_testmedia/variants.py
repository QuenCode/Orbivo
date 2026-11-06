"""The test media of the other projections (M6): which picture, which size, which metadata, which sidecar.

The metadata is chosen so that every path of the player's format detection is used once:
st3d and sv3d (VR180, cubemap 3x2), st3d only with a sidecar for the projection (EAC, fisheye),
sv3d overridden field by field by a sidecar (cubemap 6x1), and a sidecar without any metadata (cubemap 1x6).
"""

from __future__ import annotations

from dataclasses import dataclass, field

from .projections import CUBE_PRESETS, CubeLayout, Fisheye, Projection
from .spherical import SphericalMetadata

SIDECAR_LAYOUTS = {"mono": "Mono", "tb": "TopBottom", "sbs": "SideBySide"}


@dataclass(frozen=True)
class Variant:
    name: str  # file name prefix
    projection: Projection
    layout: str  # mono, tb, sbs
    sizes: dict[str, tuple[int, int]]  # size class -> frame size
    metadata: SphericalMetadata | None
    sidecar: dict = field(default_factory=dict)  # fields written to <video>.orbivo.json; empty = none
    v360: str | None = None  # ffmpeg v360 output format for the cross-check (mono only)
    description: str = ""


def fisheye_fields(fisheye: Fisheye) -> dict:
    return {"fovDeg": fisheye.fov_deg, "centerX": fisheye.center_x, "centerY": fisheye.center_y, "radius": fisheye.radius}


FISHEYE_180 = Fisheye()
FISHEYE_200 = Fisheye(200.0, 0.48, 0.51, 0.46)

VARIANTS: tuple[Variant, ...] = (
    Variant("equirect180_sbs", Projection("equirect180"), "sbs",
            {"4k": (4096, 2048), "8k": (8192, 4096)},
            SphericalMetadata("sbs", "equirect180"),
            description="VR180 Side-by-Side, je Auge 180x180 Grad, linkes Auge links"),
    Variant("cubemap3x2_mono", Projection("cubemap", cube=CUBE_PRESETS["3x2"]), "mono",
            {"4k": (3072, 2048), "8k": (6144, 4096)},
            SphericalMetadata("mono", "cubemap"), v360="c3x2",
            description="Cubemap 3x2 (rechts links oben / unten vorne hinten)"),
    Variant("cubemap6x1_mono", Projection("cubemap", cube=CUBE_PRESETS["6x1"]), "mono",
            {"4k": (6144, 1024)},
            SphericalMetadata("mono", "cubemap"), {"cubemapLayout": "6x1"}, v360="c6x1",
            description="Cubemap 6x1; sv3d sagt Cubemap, das Sidecar setzt nur das Layout"),
    Variant("cubemap1x6_mono", Projection("cubemap", cube=CUBE_PRESETS["1x6"]), "mono",
            {"4k": (1024, 6144)},
            None, {"projection": "Cubemap", "stereoLayout": "Mono", "cubemapLayout": "1x6"}, v360="c1x6",
            description="Cubemap 1x6 ohne Metadaten, Format nur aus dem Sidecar"),
    Variant("cubemap3x2_tb", Projection("cubemap", cube=CUBE_PRESETS["3x2"]), "tb",
            {"4k": (3072, 4096)},
            SphericalMetadata("tb", "cubemap"),
            description="Cubemap 3x2 Top-Bottom, linkes Auge oben"),
    Variant("eac_mono", Projection("eac", cube=CUBE_PRESETS["eac"]), "mono",
            {"4k": (3072, 2048), "8k": (6144, 4096)},
            SphericalMetadata("mono", None), {"projection": "Eac"}, v360="eac",
            description="EAC im YouTube-Layout; st3d mono, Projektion aus dem Sidecar"),
    Variant("eac_tb", Projection("eac", cube=CUBE_PRESETS["eac"]), "tb",
            {"4k": (3072, 4096), "8k": (6144, 8192)},
            SphericalMetadata("tb", None), {"projection": "Eac"},
            description="EAC Top-Bottom im YouTube-Layout; st3d, Projektion aus dem Sidecar"),
    Variant("fisheye180_mono", Projection("fisheye", fisheye=FISHEYE_180), "mono",
            {"4k": (4096, 4096)},
            SphericalMetadata("mono", None), {"projection": "Fisheye", "fisheye": fisheye_fields(FISHEYE_180)},
            v360="fisheye",
            description="Fisheye 180 Grad, Kreis mittig und bildhoch"),
    Variant("fisheye200_sbs", Projection("fisheye", fisheye=FISHEYE_200), "sbs",
            {"4k": (4096, 2048), "8k": (8192, 4096)},
            SphericalMetadata("sbs", None), {"projection": "Fisheye", "fisheye": fisheye_fields(FISHEYE_200)},
            description="Dual-Fisheye 200 Grad Side-by-Side, Kreis bewusst versetzt (Mitte 0,48/0,51, Radius 0,46)"),
)

PROJECTION_KEYS = ("equirect360", "equirect180", "cubemap", "eac", "fisheye")


def sidecar_document(variant: Variant) -> dict | None:
    if not variant.sidecar:
        return None
    return {"version": 1, **variant.sidecar}


def cube_layout_name(layout: CubeLayout | None) -> str | None:
    return None if layout is None else layout.preset
