# Save-file <-> shared-canvas pixel conversion.
#
# Bounds pulled directly from the game's own DT_WorldMapUIData DataTable
# (Pal/Content/Pal/DataTable/WorldMapUIData/DT_WorldMapUIData.uasset),
# extracted via extractor/PalExtract. Orientation (world Y -> image column,
# world X -> image row, no inversion) was verified against a real player's
# in-game HUD coordinate reading, cross-checked via the independent
# palworldlol/palworld-coord formula for the -1000..1000 HUD grid.
#
# Leaflet's CRS.Simple + imageOverlay renders a *higher* row (lat) value
# toward the top of the screen, not the lower one (confirmed empirically by
# placing the World Tree box at two different row ranges and observing
# where it actually rendered) — the opposite of what a naive PIL pixel
# lookup would suggest. Within a single image's own [0, size] bounds this
# is self-consistent for image+markers automatically; it only matters when
# placing a *second* box (the World Tree) relative to the first: the box
# that should appear higher on screen needs the numerically higher row
# range, not the lower one.
#
# The World Tree is a separate, spatially disconnected map in-game (its own
# landScapeRealPosition bounds). Community maps (e.g. IGN's) place it as a
# second landmass on the same pannable canvas, up and to the left of the
# main map, scaled proportionally by world size rather than shown at its
# native texture resolution. We do the same here so both maps share one
# Leaflet canvas instead of a separate fixed-size inset.
MAIN_MAP_BOUNDS = (-1099400.0, -724400.0, 349400.0, 724400.0)  # min_x, min_y, max_x, max_y
TREE_MAP_BOUNDS = (347351.5, -818197.0, 689148.5, -476400.0)

MAIN_TEXTURE_SIZE = 8192
_MAIN_WORLD_SIZE = MAIN_MAP_BOUNDS[2] - MAIN_MAP_BOUNDS[0]  # 1,448,800 units, square
_WORLD_UNITS_PER_PIXEL = _MAIN_WORLD_SIZE / MAIN_TEXTURE_SIZE

_TREE_WORLD_SIZE = TREE_MAP_BOUNDS[2] - TREE_MAP_BOUNDS[0]  # 341,797 units, square
TREE_DISPLAY_SIZE = round(_TREE_WORLD_SIZE / _WORLD_UNITS_PER_PIXEL)  # ~1933px at main-map scale
TREE_GAP = 200

# Row (lat) axis: Tree sits ABOVE main, so it needs the higher row range.
MAIN_ROW_OFFSET = 0
TREE_ROW_OFFSET = MAIN_TEXTURE_SIZE + TREE_GAP
# Column (lng) axis: Tree sits to the LEFT of main, so it keeps the lower
# column range and main is shifted right.
TREE_COL_OFFSET = 0
MAIN_COL_OFFSET = TREE_DISPLAY_SIZE + TREE_GAP


def _to_pixel(
    x: float,
    y: float,
    bounds: tuple[float, float, float, float],
    size: float,
    row_offset: float,
    col_offset: float,
) -> tuple[int, int]:
    min_x, min_y, max_x, max_y = bounds
    norm_x = (x - min_x) / (max_x - min_x)
    norm_y = (y - min_y) / (max_y - min_y)
    px = col_offset + norm_y * size  # column, driven by world Y
    py = row_offset + norm_x * size  # row, driven by world X
    return round(px), round(py)


def radius_to_pixels(world_radius: float) -> float:
    """World-unit radius (e.g. DT_PalSpawnerPlacement's StaticRadius) -> pixel
    radius in the shared canvas, for use with Leaflet's L.circle (which takes
    its radius in the same flat coordinate-unit space as marker lat/lng under
    CRS.Simple, confirmed empirically - see the Pal Spawn Locations frontend
    section). Both map/tree share the same _WORLD_UNITS_PER_PIXEL scale
    factor (each is independently square, main-map-units-per-pixel), so no
    per-map branch is needed here unlike locate() - only a linear scale, not
    an offset."""
    return world_radius / _WORLD_UNITS_PER_PIXEL


def locate(x: float, y: float) -> tuple[str, int, int]:
    """Returns (map_name, pixel_x, pixel_y) in one shared canvas. Checks the
    Tree's bounds first since it sits just outside the main map's edge."""
    tmin_x, tmin_y, tmax_x, tmax_y = TREE_MAP_BOUNDS
    if tmin_x <= x <= tmax_x and tmin_y <= y <= tmax_y:
        px, py = _to_pixel(
            x, y, TREE_MAP_BOUNDS, TREE_DISPLAY_SIZE, TREE_ROW_OFFSET, TREE_COL_OFFSET
        )
        return "tree", px, py
    px, py = _to_pixel(x, y, MAIN_MAP_BOUNDS, MAIN_TEXTURE_SIZE, MAIN_ROW_OFFSET, MAIN_COL_OFFSET)
    return "main", px, py
