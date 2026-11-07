# Map screenshots

`DSPRE.Avalonia --map-shots` renders map headers of a project to a PNG, or to a lossless animated
PNG (APNG) with `--animate`, without opening a window, a GL context or any editor. A GIF fallback is
available with `--format gif`. It uses the same map, tileset, building and animation data as the
Map editor, and a small software rasterizer that follows the editor's GL state. Characters are
never part of a map scene, so screenshots contain no trainers, NPCs or player.

## Usage

```bash
# Everything, into ./mapshots
./mapshots.sh

# A few maps, with a different output folder
./mapshots.sh --maps 47,133 --out shots

# Every gym or cave area whose name contains a word
./mapshots.sh --filter gym

# Re-render files that already exist
./mapshots.sh --force

# List headers
./mapshots.sh --list

# What a header contains, without writing an image
./mapshots.sh --info --maps 47

# Lossless animated APNG at the still PNG's 1024x1024 resolution
./mapshots.sh --maps 221 --animate --frames 120 --scale 16 --out mapshots

# Smaller 255-colour GIF fallback
./mapshots.sh --maps 221 --animate --frames 120 --scale 8 --format gif --out mapshots
```

`mapshots.sh` builds the project first. The executable can also be called directly:

```bash
dotnet DSPRE.Avalonia/bin/Release/net8.0/DSPRE.Avalonia.dll --map-shots \
  --rom "Pokemon - Platinum Version (USA) (Rev 1)_DSPRE_contents" --out mapshots
```

`--rom` takes a ds-rom/ndstool project folder. With no folder, a single `*_DSPRE_contents` folder
in the current directory is used. A `.nds` path is accepted only when its extracted
`..._DSPRE_contents` sibling exists; DSPRE extracts a ROM the first time it is opened in the app.
The game code is read from the project name (`Platinum`, `HeartGold`, …) or `--game CPUE`.

Options:

| Option | Meaning |
|---|---|
| `--rom <folder>` | Project folder. Defaults to the one `*_DSPRE_contents` folder here. |
| `--game <code>` | `CPUE`, `CPEU`, `CPUP`, `CPJP`, `IPKE`, `IPGE`, `ADAE`, `APAE`. |
| `--out <dir>` | Output folder, `mapshots` by default. |
| `--config <file>` | Settings file, `mapshots.json` here by default. |
| `--maps 5,35` | Only these header numbers. |
| `--filter <text>` | Only headers whose name contains the text. |
| `--force` | Replace existing images. Without it a non-empty image is left alone. |
| `--scale <n>` | Pixels per map tile. Default 16. |
| `--width`, `--height` | Fixed canvas size. Default is fitted and rounded up to 512. |
| `--pitch <degrees>` | Camera angle. 90 is straight down, 50 matches the reference rips. |
| `--cells 1,0;1,1` | Draw only these matrix cells of the header. |
| `--shade` | Apply vertex colours where the model disables shading, for maps whose shadows are baked that way. |
| `--light` / `--no-light` | Draw or omit directional face lighting (on by default). |
| `--light-dir x,y,z`, `--light-ambient n`, `--light-diffuse n` | Override the lighting direction and strength for calibration. |
| `--shadows` / `--no-shadows` | Draw or omit the baked cast-shadow overlay (on by default). |
| `--hide <names>` | Extra material names to leave out (comma-separated). |
| `--list` | Header numbers and names. |
| `--info` | Matrix, cells, content bounds and suggested canvas for the selected maps. |
| `--materials` | With `--info`, dump every material and its texture. |
| `--pick x,y` | Print the material that drew a pixel, for diagnosing a texture. |
| `--animate` | Write an animated APNG (or GIF with `--format gif`) instead of a PNG. |
| `--frames <n>` | Frames sampled from one animation loop. Default 120. |
| `--format apng` / `--format gif` | Animated format. APNG is lossless and the default; GIF is smaller and limited to 255 colours. |

## How the reference look is reproduced

The five reference images use:

- **Orthographic camera, pitch 50°.** A straight top-down view hides the fronts of walls. At 50° the
  wall faces and raised platforms match the references while floor tiles stay square and parallel,
  which rules out perspective.
- **Content framing.** The canvas is centred on the visible geometry and rounded up to 512 pixels
  wide and 512 or 1024 high. Oreburgh, Veilstone and Pastoria come out at 512×512, 512×512 and
  512×1024, exactly as the references.
- **16 pixels per map tile**, nearest-neighbour textures, no mipmaps, no antialiasing.
- **Map textures and the area's building tileset**, including the baked per-vertex shading and the
  baked cast-shadow materials. Directional face lighting is on by default and was calibrated
  against Hearthome 086's roofs and walls. The hidden `lambert1` void layer is not drawn, and
  `hideMaterials` removes room-specific backdrops such as Veilstone's opaque black `lambert8`
  plane. `shadows: false` (or `--no-shadows`) removes only the cast shadows.
- **Transparent background** with binary alpha.
- **No overworld sprites.** Only geometry from the map files and building models is rendered.

The renderer follows the editor's shader: texture × vertex colour, with optional directional face
lighting, the material's texture matrix, the same alpha-test thresholds, the same face culling and a
depth buffer. Cut-out textures (trees, puddles, cliff rims) are drawn in the opaque pass and write
depth; only materials with alpha below solid are blended. Keeping every cut-out in the blended pass
let later overlays paint across tree canopies and raised rims.

## Per-map settings

`mapshots.json` has a `defaults` block and a `maps` list. Each entry matches a header by `id` or by
`name` (the header's location or internal name). Entry fields override the defaults:

```json
{
  "id": 133,
  "file": "VeilstoneGym.png",
  "pitch": 50,
  "pixelsPerTile": 16,
  "width": 512,
  "height": 512,
  "frame": "content",
  "offsetXTiles": 0,
  "offsetZTiles": 0,
  "shadows": true,
  "lighting": true,
  "hideMaterials": [ "lambert1", "lambert8" ],
  "cells": [ "0,0", "0,1" ],
  "stack": [ 156, 155, 154 ],
  "gap": 10,
  "background": "#ffffff",
  "enabled": true
}
```

- `frame` is `content` (visible geometry, the reference look) or `cell` (the header's whole matrix
  cells).
- `offsetXTiles` / `offsetZTiles` shift the camera on the map grid, for a map that needs a nudge.
- `stack` renders this header and the listed headers separately and packs them vertically, centred,
  with `gap` transparent rows between them. It is how the three Sunyshore Gym rooms are combined.
- `shade: true` applies vertex colours where the model marks shading off. It restores some baked
  shadows but also tints the whole scene, so set it per map rather than globally.
- `lighting` is on by default and applies directional face lighting to opaque textured geometry. Set
  `lighting: false`, or pass `--no-light`, for a map that should stay flat. The Hearthome reference
  calibrated the light direction and strength.
- `hideMaterials` extends the default `lambert1` void layer. Veilstone also needs `lambert8`, an
  opaque black backdrop plane that otherwise covers its right wall. Each entry names one material in
  full and is matched case-insensitively, so `lambert1` leaves a wall material such as `lambert10` alone.
- `enabled: false` leaves a header out of a full batch.
- `animate: true` writes an APNG instead of a PNG. `format` is `apng` (default) or `gif`; `frames` samples the animation loop, 120 by default. An animated entry cannot use `stack`. See below for the clock, APNG and GIF limits.

The five reference maps are pinned in `mapshots.json`: Oreburgh Gym 47, Pastoria Gym 122,
Veilstone Gym 133, Sunyshore Gym 156+155+154, Spear Pillar 220. Directional lighting applies to
all maps through the defaults block unless an entry sets `lighting: false`.

## Animated captures

`--animate`, or `"animate": true` on a map entry, renders the scene on the field's 30-steps-a-second
clock instead of as one still. It uses the same building animator as the map view:

- building texture scrolls and swaps, material fades, joint motion and whole-model motion;
- the area's terrain scroll on HeartGold and SoulSilver;
- `fldtanime` texture swaps on the map's own materials.

The loop is the least common multiple of the animation lengths, capped at 600 frames. A scene
whose combined loop is longer repeats at 600 frames rather than at a perfect loop. `--frames N`
samples that loop down to at most N frames. The default 120 frames turns a 240-frame loop into 120
frames. A scene with no finite loop uses N frames at 30 Hz. Animated captures must name one header;
an entry with `stack` is rejected.

Animated output is APNG by default: 8-bit RGBA, lossless, with exact `step/30` frame delays, one
full-canvas frame per step and an infinite loop. APNG keeps the still PNG's pixels and transparency,
so a 16 px/tile capture matches the still image exactly. Files are larger than GIF; the header 221
example is about 18 MB. `--format gif` selects GIF89a instead, with one global palette of up to 255
colours plus index 0 for transparency, disposal method 2 and an infinite loop. GIF is smaller and
universally readable, but a map with more than 255 colours loses fidelity; header 221 has 889
colours. GIF delays are hundredths of a second, so its 30 Hz timing is rounded. APNG delays are
exact.

Header 221 (`221 Spear Pillar`) exercises all of this. Its 2x2 summit has 120-frame texture scrolls
on buildings 579 and 580 and a 240-frame joint animation on building 581; the joint animation is the
visible portal motion. This entry reproduces it:

```json
{
  "id": 221,
  "file": "221 Spear Pillar.apng",
  "pixelsPerTile": 16,
  "animate": true,
  "frames": 120,
  "format": "apng"
}
```

At `pixelsPerTile: 16` the fitted canvas is 1024x1024, the same as the still PNG, and the first APNG
frame is byte-identical to `221 Spear Pillar.png`. At 8 the canvas is 512x512, half the resolution,
which is why the earlier GIF looked pixelated. `--format gif --scale 8` is available when a smaller,
broadly supported file matters more than exact colour.

## Measured reference properties

| Reference | Header | Size | Content | Camera |
|---|---|---|---|---|
| OreburghGym.png | 47 `C03GYM0101` | 512×512 | 230×392 | pitch 50, 16 px/tile |
| VeilstoneGym.png | 133 `C07GYM0101` | 512×512 | 384×392 | pitch 50, 16 px/tile |
| PastoriaGym.png | 122 `C06GYM0101` | 512×1024 | 400×531 | pitch 50, 16 px/tile |
| SunyshoreGym.png | 156/155/154 | 512×1024 | three rooms | pitch 50, packed |
| SpearPillar.png | 220 `D05R0114` | 512×1024 | 351×723 | pitch 50, 16 px/tile |

Content sizes are measured on the references' alpha. Veilstone comes out 386×379, Pastoria 400×529
and the Sunyshore stack 336×941, within a few percent of the rips. Oreburgh is 256×410 against
230×392 because our scene includes the outer rock ring the rip cropped away; `offsetXTiles` /
`offsetZTiles` shift that framing if a tighter crop is wanted. Spear Pillar's full 2×2 summit is
wider than the reference's 351×723 window, so use `cells` or a smaller `width` to isolate the
platform.

## Known limits

- Without `--animate`, texture and terrain animations are frozen at their resting frame. Water
  sparkle, animated gym mats and scripted map changes (Pastoria's raised platforms, for example)
  show the default state, not a particular in-game moment. This is why Pastoria's moving platforms
  and wave layer differ from the reference rip.
- Animated captures follow the automatic field animations only. Scripted changes, doors, time-of-day
  triggers and the wave layer still show the default state.
- APNG is lossless but larger than GIF. GIF is limited to 255 colours, so use APNG for pixel-art
  captures that must match the still PNG.
- `--no-shadows` removes the baked cast-shadow materials. It does not remove the black backdrop
  planes, which are ordinary opaque geometry; hide those per map with `hideMaterials`.
- Overworld sprites are not part of a map scene. NPCs, the player, trainers and sprite props such as
  Veilstone Gym's punching bags are therefore absent, while static map and building geometry is
  drawn.
- Spear Pillar's pillar shadows are not in header 220's overlay. Header 221 has a `kage` layer, but
  it is the cutscene portal, not the pillar shadows. The pillar shadows are baked into vertex
  colours while the model marks every part as unshaded; `--shade` restores them but also tints the
  room olive, so it is opt-in.
- Map-shot lighting is a single directional approximation using geometric face normals and the
  material's ambient/diffuse colours. It does not reproduce the full NDS four-light setup, per-vertex
  normals, specular highlights, or time-of-day area lighting.
- Sunyshore's cog platforms are not in the rooms' map data. No map's building list places the
  `gym_gear01` to `gym_gear04_2` models (0457 to 0464), and each room's map model is flat, so the
  still shows the dark floor where the game's rotating platforms stand. The wider door surrounds
  belong to cells outside each 1x1 header, so the packed image shows each header's own cell plus its
  shadow overlay, not the neighbouring floor's border.
- Spear Pillar's reference is a 512×1024 window on the 2×2 summit matrix; the batch renders the
  whole summit, and its `mapshots.json` entry fixes the canvas at 512×1024. `cells` can isolate one
  strip of the matrix. The reference's tighter platform framing is not reproduced exactly.
- The Sunyshore reference is three connected rooms; the config renders headers 156, 155 and 154 and
  packs them into one 512×1024 image. Each header's own cell stops short of the neighbouring
  floor's hazard-striped border, so the packed rooms are slightly shorter than the reference.
- A map whose building tileset is missing renders untextured rather than stopping the batch.
- The rasterizer is the CPU fallback, not the OpenGL view. It intentionally skips fog, the
  permission tint, the tile grid, highlights, sprites and 2D screen layers, which a map rip has none
  of.

## Output

Files are named `NNN ` plus the header's place name (for example `120 Pastoria City.png` and
`399 Route 224.png`), or the `file` in a config entry. Animated entries use `.apng`, or `.gif` with
`--format gif`. The number keeps names unique where two headers share a place name. The batch reports
each header, skips files that already exist unless `--force` is given, and carries on after a failure,
ending with a count and a non-zero exit code if anything failed.

The five renders used to calibrate the camera and config are in `reference_shots/`. Recreate them
with `./mapshots.sh --maps 47,122,133,156,220 --out reference_shots --force`.
