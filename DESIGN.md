# Fink (fade ink): design

Fink is a frame-by-frame 2D animation painter built for one workflow: hard-line, palette-based
cel animation. It exports sprite sheets, extra data channels and metadata that drop straight into
games built on [fade.mono](../fade.mono).

"Fink" is a working title.

## Goals

- Cel animation, frame by frame, with onion skinning.
- **Palette-indexed** color. Every pixel is a palette index, never RGBA.
- **Line and fill layers kept separate, and enforced.** Line tools only draw on line layers, and
  fill tools only paint fill layers.
- **Data channels** painted on the same pixel grid as the color: normal, emissive/fx, and any
  number of custom labeled channels.
- **Live shader preview** using the game's real `.fx` effects, with generated inputs for every
  effect parameter and for the per-sprite TEXCOORD1/2 data.
- One-click export into a fade.mono game's `Assets/`. A running Debug build of the game picks the
  change up through its existing hot reload.
- An undo history you can see and click back through, with named checkpoints.
- Saved brushes.
- Works well with an XP-Pen used as a mouse. Pressure is **not** in v1; input samples carry a
  `Pressure` field fixed at 1.0 so it can be added later.

## Non-goals (for now)

- Painterly brushes, antialiasing, blending modes.
- FadeBasic scripting inside the editor.
- Vector or bone animation.

---

## Architecture

```
C:/proj/fink/
  Directory.Build.props     version pins + FadeMonoDir, mirroring soundrel's
  Fink.sln
  src/Fink.Core/            document model, tools, undo, export. Pure C#, no MonoGame.
  src/Fink.Editor/          editor state, commands, keymap + hotkeys, settings, script runner. No window.
  src/Fink.App/             the MonoGame + ImGui window: draws Fink.Editor's state, feeds it input
  tests/Fink.Core.Tests/    unit tests for Core
  tests/Fink.Editor.Tests/  editor tests; also runs every tests/scripts/*.fink-test headless
  tests/scripts/            automation scripts (headless in tests; with screenshots in the app)

C:/proj/fade.mono/
  Fade.MonoGame.Anim/       (planned) runtime animation library; see "Engine side"
```

- **Fink.Core** has no graphics dependency, so the model, tools, undo and export are all testable
  headless.
- **Fink.App** references `Fade.MonoGame.Game` and `.Contracts` by source from `../fade.mono`,
  exactly as soundrel does. The version pins in `Directory.Build.props` must match fade.mono's
  (FadeVersion, MonoGame 3.8.6, ImGui 1.91.6.1), or the build fails with CS1704.
- Fink has its own `Game` subclass. The engine's `Game1` expects a FadeBasic program, and its
  systems are static and global. Fink reuses only the parts that stand on their own:
  - `ImGuiRenderer` (docking is already on)
  - `FadeSpriteBatch` / `FadeSpriteVertex` (TEXCOORD1/2), for the shader preview
  - the in-process content builder (`Fade.MonoGame.Content`), to compile game `.fx` files

## Selection and transforms

- **Tools:** Select (M, rectangle), Lasso (Q), Wand (W, same palette index, contiguous or
  global), Transform (T, handles).
  - Shift adds, Alt subtracts, Shift+Alt intersects. While a selection tool is active, Alt does
    not trigger the eyedropper.
- **The selection is one document-wide mask**, shared by all layers and frames. Painting, fills
  and erasing stay inside it.
- **Floating selections.** Dragging inside a selection picks its pixels up into a
  `FloatingSelection`. The pixels float above their layer until applied (Enter / clicking
  outside / changing tool, frame or layer) or cancelled (Esc / Ctrl+Z).
  - Picking up and applying is **one undo step**.
  - Transparent pixels never stamp.
- **Linked layers move together** (on by default; "Selection Includes Linked Fill"): a
  selection on a line layer also lifts, transforms, copies and clears the fill layer that
  follows its timing, and the other way round, when that layer has pixels in the selection.
  A copied line + fill pastes onto the current line + fill by role, whatever they're named.
- **"Selection Affects All Layers"** lifts every visible, unlocked line and fill layer together,
  so line art and its fill move as one.
- **Transform** is center + scale (negative flips) + rotation + skew (`TransformParams` →
  `Affine`):
  - Handles: corners and edges scale (Shift keeps the shape), outside the corners rotates
    (Shift snaps to 15°), inside moves. Scaled sizes are whole pixels.
  - Numeric fields in the Tools panel.
  - Flip H/V (Shift+H/V), rotate ±90 (R/Shift+R), nudge (arrows over the canvas, Shift for
    8px). These act on the selection, or the whole layer when nothing is selected.
- **Sampling:**
  - Quarter turns, flips and whole-number scales are pixel-exact.
  - Any other angle or skew samples an 8× Scale2x upscale (the RotSprite idea), so 1px lines stay
    connected.
  - Everything is palette indices; nothing is blended.
- **Clipboard:**
  - Ctrl+C/X/V, internal for now.
  - Paste lands **in place**, floating, so copying a pose to the next frame lines up.
  - One copied layer pastes onto the current layer; several paste onto the same-named layers.

## Commands and hotkeys

- **Every action is a `Command`** (id, title, category, description, enabled/checked state,
  default bindings). Menus, buttons, tooltips, the command palette, the Options screen and
  automation scripts all go through the same registry, so shortcut labels can't be wrong.
- **Bindings** are a chord (key or Middle/Mouse 4/Mouse 5 + Ctrl/Shift/Alt) in a **scope**:
  Global, Canvas (mouse over the canvas) or Timeline (the Timeline panel). Scoped bindings win over
  Global ones. A command can have several bindings.
- **Hold commands** run on key down and undo on key up: Alt for the eyedropper and Space or Middle
  Mouse for pan (Canvas scope).
- **Keymap**: defaults live in code; `%APPDATA%/Fink/keymap.json` stores only the commands you
  changed, so new defaults still arrive.
- **Options → Hotkeys**:
  - search by name, or press "Find by key"
  - click a shortcut to rebind it, right-click to change its scope or remove it, + to add one
  - conflicts show in red with a one-click fix
  - reset one command or all of them
- **Command palette**: Ctrl+Shift+P / F1.

## Layout

- **Workspaces** are dock layouts:
  - built in (Ctrl+1 Draw, Ctrl+2 Animate, Ctrl+3 Minimal), built in code with ImGui's
    DockBuilder (P/Invoked from cimgui; ImGui.NET doesn't wrap it)
  - saved by the user (ImGui ini + which panels are open, in `%APPDATA%/Fink/workspaces/`)
- **View menu** toggles each panel. **Tab** hides them all (focus mode). **F11** is fullscreen.
- **UI scale** (Options → Interface) rebuilds the font (Segoe UI) and scales every style size.

## Testing and automation

- `dotnet test`: Core unit tests, editor tests, and every `tests/scripts/*.fink-test` run
  headless.
- `Fink.exe --script tests/scripts/visual-tour.fink-test` runs a script in the real window:
  - saves screenshots (`shot name` for the window, `shot name canvas` for the flattened frame) and
    `report.txt` to `tests/scripts/out/<script>/`
  - exits 0 on pass, 1 on fail
  - uses a fresh settings folder, so it never touches your own layout or keys
- The script language is documented on `ScriptRunner`:
  - `cmd`, `key`, `keydown/keyup`, `hover`, `stroke`, `drag`
  - `tool`, `color`, `layer`, `frame`, `set`, `bind`
  - `assert`, `wait`, `shot`

## Document model

```
Document
  Width, Height
  Palette                 up to 256 entries; index 0 = transparent
  Channels[]              color (always present) + data channels (normal, fx, custom...)
  Fps                     one fixed rate; every frame lasts 1/fps
  Layers[]                each has a Role: Line | Fill | Reference | Data
    Drawings[]            numbered 1, 2, 3...; each owns its cel (one byte grid per channel)
    Follows?              another layer whose timing this one uses (a Fill follows its Line)
  Frames[]                columns of the dope sheet; identity only
  Exposures[layer, frame] which drawing an own-timing layer shows there, or nothing
  Tags[]                  named frame ranges ("walk", 0..7, loop)
  Points[]                named per-frame points (pivot, hand, muzzle...)
```

- **Color cels** are `byte[]` palette indices, which makes undo snapshots small and
  palette swaps free.
- **Data channels** are their own grids:
  - `normal`: 2 bytes per pixel (x, y), painted from a "normal palette" of directions, or
    generated from a height channel.
  - `fx` / emissive, and custom channels: 1 byte per pixel, painted as values 0..255.
- **Layer roles** do the line/fill enforcement:

| Tool | Allowed on |
|---|---|
| Pencil, line, curve, shapes, eraser | Line |
| Fill, fill brush, shade brush, eraser | Fill |
| Channel brushes | Any layer, when its data channel is active |
| Nothing | Reference (shown, never exported) |

  Using a tool on the wrong layer does nothing and shows a "wrong layer" cursor. A setting can
  make it switch to the nearest matching layer instead.
- **Character groups** (later): a Line layer paired with its Fill layer, so moving, copying and
  onion skinning treat the pair as one unit. Following timing (below) is the first piece of
  this.

### Dope sheet: drawings and exposures

Pixels belong to **drawings**, not to (layer, frame) pairs. The timeline says which drawing each
layer shows on each frame:

```
            1    2    3    4    5    6    7    8    9
Line        [1 ─────][2 ][3 ─────][2 ─────][1 ─────]    held, reused (a cycle)
Fill        [1 ─────][2 ][3 ─────][2 ─────][1 ─────]    follows Line by number (3 not filled yet)
BG          [1 ─────────────────────────────────────]   one drawing held all shot
```

- **Hold:** the same drawing on consecutive frames. Editing it changes every frame.
- **Reuse:** the same drawing again later (Show Drawing in the cell menu). Cycles cost nothing.
- **Fixed rate:** frames have no durations. A drawing lasts longer by being held; export turns
  holds back into per-sprite durations.
- **Following:** a layer can take another layer's timing. It then shows *its own* drawing with
  the same number as the source's, so fill N always goes with line N through holds, reuse and
  duplication. New Fill layers follow the Line layer they were added under; the cell menu's
  Timing submenu changes it. Giving a layer its own timing keeps what it shows.
  Without this, filling a held line drawing would silently fill every frame showing that fill.
- **Drawing on an empty cell** makes a new drawing exposed there, undone together with the
  stroke (and dropped if the stroke changes nothing). A follower can't be painted where its
  source shows nothing.
- No per-exposure offsets/movement: holds are still pictures.

## Tools (v1)

- **Pencil:**
  - pixel perfect (removes L-corners)
  - size 1..n, round or square tip
  - stabilizer
- **Eraser** (writes index 0).
- **Line** with angle snapping (Shift).
- **Rectangle and ellipse**, outline or filled.
- **Fill:**
  - exact index match
  - contiguous or global
  - boundary from the paired line layer, or from all visible line layers
  - **gap closing** (0..3 px)
- **Selection:**
  - rectangle, lasso, magic wand (by index)
  - add, subtract and intersect
  - move with nearest-neighbor transforms, flips and 90° rotations
  - moving a selection carries all of its channels with it
- **Eyedropper** (Alt). Picks the palette index, not the RGB value.
- **Brushes:** tool + size + tip + stabilizer + pixel-perfect + target channels. Saved as presets
  in a user library (`%APPDATA%/Fink/brushes.json`).

## Animation (v1)

- **Timeline:** a horizontal dope sheet. Layers are rows (top of the stack first), frames are
  columns, each hold is one block labelled with its drawing number. Seconds are marked.
  - Click a cell: go to that frame and layer. Drag the header to scrub.
  - Drag a block's middle to move it; Ctrl+drag copies it (reuses the drawing). Drag either
    end to change where the hold starts or ends. Blocks cover the cells they land on and leave
    empty cells behind; dragging past the last frame adds frames.
  - Right-click a cell: New / Duplicate Drawing, Show Drawing (reuse), Clear Cell, holds,
    Timing (own, or follow a layer).
- **Frame commands:**

| Command | Key | Does |
|---|---|---|
| New Frame | Alt+N | inserts a frame; the current layer gets a new blank drawing, others hold |
| Duplicate Frame | Alt+D | same, with a copy of the current drawing (and its fill) |
| Insert Held Frame | Alt+H | inserts a frame where everything holds |
| New / Duplicate Drawing | N / D | replaces the current drawing from here to the end of its hold |
| Hold Longer / Shorter | > / < | the current hold grows or shrinks; the rest of the layer slides |
| Delete Frame | Alt+Delete, Delete over the timeline | removes the column |

- **Onion skin by drawing:** N drawings before and after on each layer, skipping holds and
  empty cells, tinted, with adjustable opacity. On twos, the previous frame is usually the
  same drawing, so frame-based onion skin would show nothing useful. Each layer's ghosts are
  drawn just under that layer, so an opaque background or reference layer below can't hide them.
- **Tags:** named frame ranges (walk, idle) with a loop mode: loop, ping-pong or play once.
  They live in the timeline's top row: drag across empty space to make one, drag its ends to
  resize it, right-click to rename, change the loop, play or delete. A tag holds its first and
  last *frames*, so frames inserted inside it make it longer; deleting an end frame moves the
  end inward.
- **Playback:** fixed rate. With "Play Tag Only" on (the default), Play loops the tag under the
  playhead in its loop mode; otherwise the whole timeline.
- **Per-frame points:** a pivot plus named points.

## Undo history *(branching: built)*

- **One history for the whole project.** It covers drawing in any asset, timing, tags, points, the palette (colors, ramps, variants), data channels, adding, renaming and deleting assets, and atlas settings.
  - Each step is tagged with the asset it changed. Undo and redo switch to that asset, so you see what happened.
  - Each asset still remembers its own frame and layer.
- **It branches instead of forgetting.** Undo some steps and then change something, and a new branch starts there. The undone steps stay on their own branch.
  - Clicking any step on any branch goes there: back to where the branches meet, then down the other one.
  - Redo follows the branch you were on last.
- **The History panel is a chart.**
  - One dot per step, in the order made. Branches get their own lanes.
  - The path to where you are is highlighted, steps Redo would take are plain, and other branches are dim.
  - Right-click a step to name it (a checkpoint you can find again).
- **How steps are recorded:**
  - Pixel edits are tile deltas: the before and after bytes of the 32×32 tiles they touched.
  - Structural edits are snapshots of the asset's structure.
  - Project changes are snapshots of the project-level state (`ProjectState`). Assets are kept as objects, so deleting one and undoing brings back the same asset. Atlas placements are left out, so undo never reshuffles the atlas.
  - Panels that edit the project directly (Palette, Atlas) are watched. When you let go of a control, whatever changed becomes one step, so a whole color-picker drag is one step.
- **Unsaved changes** means the history isn't at the step that was saved. Undoing back to it counts as saved again.
- **Size:** past 2000 steps the oldest are folded into the start, as long as that doesn't cut a branch.
- **Later:** thumbnails per step.

## Pen

- **Flipping an XP-Pen (or any Windows Ink pen) to its eraser end erases**, whatever tool is chosen, until you flip it back. It's the momentary Eraser, like holding Alt for the eyedropper.
- **How it works:** Fink listens for Windows pointer messages on its own window (SDL doesn't pass the pen's "inverted" flag on) and passes every message on unchanged.
- **Driver setting:** it needs Windows Ink on in the tablet driver. Options > Interface > Pen shows whether a pen has been seen.
- **Fallback:** without Windows Ink, have the driver send a key for the eraser end and bind that key to **Eraser (hold)**.
- **Turning it off:** Options > Interface > Pen.

## Shader preview

A **Shader** panel draws the current frame the way a fade.mono game draws a sprite.

- **Same renderer:** fade.mono's `FadeSpriteBatch`, with the engine's baked
  `FadeSpriteBatchEffect` (loaded from Fade.MonoGame.Game's resources, as `Game1` does). Game
  shaders that read FadeSpriteBatch's vertex format (texcoord1/texcoord2) work unchanged, and
  the batch fills in `MatrixTransform`.
- **Same compiler:** `.fx` files are compiled in process with MonoGame's content pipeline
  (`PipelineManager`, EffectImporter/EffectProcessor) for DesktopGL, which is what Fink runs on.
  Compiling runs off the UI thread; errors show in the panel with file, line and column.
- **Hot reload:** the shader is recompiled whenever it or any `.fx*` beside it (its includes)
  changes. The last good compile stays on screen while a broken one shows its error.
- **Samples** in `samples/shaders`, shipped next to the exe and listed under "Fink samples":
  outline, hit flash, dissolve, wave, drop shadow, rim light, hue shift. They share
  `FinkSprite.fxh` (FadeSpriteBatch's vertex format), so they drop into a game as they are, and
  each documents its parameters and the per-sprite texcoord values it reads.
- **Defaults** come from the source (`float Amount = 0.5;`), since MonoGame keeps initial values
  only on some backends.
- **Picking:** a folder setting (a game's `Assets`) lists every `.fx` under it; "(engine sprite
  shader)" shows the plain sprite for comparison.
- **The sprite:** visible non-reference layers, palette resolved, alpha premultiplied like the
  content pipeline does. Zoom, a margin around it for outlines and glows, background color,
  point or linear sampling.
- **Inputs, generated from `Effect.Parameters`:**
  - `float`, `float2-4`, `int`, `bool` → fields; float3/4 named color/tint → color pickers
    (right-click the name to switch).
  - Each float can instead come from **time** (seconds, running), **1 / sprite size** or
    **sprite size**; names with "time" / "texel" pick those automatically.
  - Textures → this sprite, or a PNG file. `SpriteTexture`/`Texture` are the sprite.
  - Matrices and arrays are listed but not editable.
  - **Per-sprite values:** the sprite's color, texcoord1 and texcoord2, as the game sets them.
- Inputs are remembered per shader file in the settings folder (`shader-preview.json`).
- Later: data channels (normal, fx) as texture inputs; a DesktopVK compile for Vulkan games;
  flat / shaded / split views.

## Export contract

Export writes into a game's `Assets/Textures/<path>/`:

| File | Content |
|---|---|
| `<name>.png` | color atlas: palette resolved to RGBA |
| `<name>.<channel>.png` | one per data channel, **same layout**, RGB with **no alpha** |
| `<name>.anim.json` | metadata sidecar (below) |
| `<name>.fbasic` (optional) | generated constants, for games without the anim library |

- **Done (v1):** File > Export Sprite Sheet (Ctrl+E), Export Again (Ctrl+Shift+E). Folder and
  name are remembered. Uniform grid, optional columns and padding. No `.fbasic` or data
  channels yet.
- **Holds and reuse:** each distinct drawing combination is packed once. A hold becomes one
  sprite frame with `ms = frames × 1000 / fps`; reuse points two frames at the same rect.
- **Layout:** a uniform grid by default, which works today with `set texture frame grid`.
  Packed/trimmed layouts later, which use `set texture frame rect` plus per-frame offsets.
- **Data channel PNGs** must be built with premultiply **off** (the `texture premultiply`
  macro / `ContentEntry`), or the pipeline corrupts the data.

The sidecar `anim.json`:

```json
{
  "version": 1,
  "image": "Hero",
  "fps": 12,
  "cell": { "w": 64, "h": 64 },
  "grid": { "cols": 3, "rows": 1 },
  "padding": 0,
  "cells": [ { "x": 0, "y": 0, "w": 64, "h": 64 }, ... ],
  "frames": [
    { "cell": 0, "x": 0, "y": 0, "w": 64, "h": 64, "ms": 167, "timelineFrom": 0, "timelineTo": 1 }
  ],
  "tags": [ { "name": "walk", "from": 0, "to": 4, "loop": "forward" } ]
}
```

The generated `.fbasic` is the zero-dependency fallback. It holds frame counts, per-frame ms,
tag ranges and points as constants, following the pattern of `pdnsheet`'s
`fish_routines_atlas.fbasic`.

## Engine side: `Fade.MonoGame.Fink` *(first version built; see samples/FinkSample)*

An opt-in library in fade.mono (`fade.mono/Fade.MonoGame.Fink`), set up like
`Fade.MonoGame.Steam`. It builds on the engine's existing systems and changes none of them.

- **What Fink exports per atlas:**
  - `<atlas>.png`: palette indices in the red channel, alpha 0/255
  - `<atlas>.palette.png`: 256 wide, one row per variant
  - `<atlas>_palette.fx`: the palette shader. Pixel shader only, with GL and Vulkan branches, compiled by the game's own content build
  - `<atlas>.atlas.json`: the manifest
  - `fink_<atlas>.fbasic`: the bindings
- **No strings at runtime.** Clip (tag) ids are unique in the project, stable across exports and never reused. Names in the manifest are for people only.
- **Generated bindings:**
  - `#constant TAG_<ASSET>` for an asset's whole timeline, `TAG_<ASSET>_<TAG>` for each tag, and `PAL_<VARIANT>` for each palette row
  - `FINK_<ATLAS>_STAMP`
  - `FUNCTION fink_load_<atlas>()`
  - The file must be first in the game's FadeSource list, because constants are text replacement.
  - Names that clean up to the same identifier stop the export with a message.
- **How it fits the engine:**
  - An atlas becomes one texture (a frame per picture, plus an empty frame), one palette texture and one effect, under ids from 1,900,000,000 up.
  - The textures load through the content pipeline, so they hot-reload. The default premultiply is harmless for 0/255-alpha indices, and the shader un-premultiplies the palette.
  - The manifest is read from the project's Assets in Debug (so it is watched and re-read when Fink exports again), otherwise from beside the built content. The game's csproj copies `*.atlas.json` there, because the content build skips `.json`.
- **Playback:** `FinkSystem` is an `IFadeHostSystem`. In `BeforeVmTick` it advances every playing sprite by milliseconds, so holds work, and sets the sprite's texture, effect, frame, origin and texcoord1.x (the palette row). The origin is the frame's pivot from Fink, or an anchor the game pins with `set fink sprite anchor` (a negative x goes back to the pivot).
- **Points:** `fink sprite point x/y(spr, POINT_X)` say where a named point is on screen, through the sprite's position, scale, flip and rotation.
- **Maps:** each map loads as a texture; `fink atlas map(atlas, MAP_X)` and `fink atlas palette(atlas)` return texture ids for your own shaders. It also sets the effect's Palette and PaletteRows every frame, because a hot-reloaded effect loses its parameters. F1 clears playing sprites.
- **Commands:** `fink atlas load`, `play/loop/stop/resume/release fink sprite`, `set fink sprite palette/speed/anchor/frame`, `fink sprite clip/playing/done/frame`, `fink clip frames/ms`, `fink palette rows`. The sample's README lists them.
- **Opting in:**
  - a ProjectReference
  - `<FadeCommand Include="Fade.MonoGame.Fink" FullName="Fade.MonoGame.Fink.FadeFinkCommands" />`
  - `new FinkSystem()` registered as the IFadeHostSystem (an `IFadeHostSystem[]` alongside Steam)
  - the atlas.json copy item
  - the generated file listed first among the FadeSource items
- **Later:**
  - content rules (`IContentRules`) for indexed maps
  - data maps and channel routing
  - the manifest schema shared between Fink and the library instead of being mirrored
  - a generated .fxh that reads the maps by channel name


## Projects, saving and recovery *(done)*

- **A project** is a set of **assets** (each an animation: its own size, layers, dope sheet,
  tags) sharing one **palette**. Assets are tabs above the canvas (+ adds one; right-click to
  rename, duplicate, delete; Ctrl+PageUp/PageDown step through). Each asset keeps its own undo
  history and its place (frame, layer).
- **The palette** is grouped into **named ramps** (Ink, Warm, Cool, Green, Skin, Accent by
  default). "+ Ramp" adds five shades of the primary color as a new ramp; right-click a ramp's
  name to rename it. **Variants** (other color sets for the same entries) are in the model and
  file; their UI comes with the atlas work.
- **File > New / Open / Open Recent / Save / Save As** (Ctrl+N, O, S, Shift+S), Windows file
  dialogs. `Fink.exe game.fink` opens a file. New, Open and Quit (menu, Ctrl+Q or the window's
  close button) ask "Save changes?" first when there are any. The title and the current tab
  show when there are unsaved changes.
- **Saving is atomic:** written to `<file>.saving`, then moved over the old file.
- **Recovery:** while there are unsaved changes, the whole project is autosaved every 20 s to
  `%APPDATA%/Fink/recovery/run-<time>-<id>.fink` (never over the real file). Saving, or quitting
  normally (including "Don't Save"), deletes it. So a recovery file found at startup is work
  from a run that crashed or was killed: Fink offers Recover / Delete / Later. Recovered work
  opens as unsaved, pointing at the file it was last saved as.

### File format (`.fink`, version 1)

A zip:

```
fink.json                    { format: "fink", version, name,
                               palette: { entries: ["#rrggbbaa"...], ramps: [{name, start, count}],
                                          variants: [{name, entries}] },
                               assets: [names in order] }
assets/<n>/asset.json        { name, width, height, fps, frames,
                               layers: [{ name, role, visible, locked, follows: <layer index>?,
                                          drawings: [numbers], exposures: [number|null per frame] }],
                               tags: [{ name, from, to, loop }] }     (layers bottom first)
assets/<n>/L<layer>_<drawing>.bin   one byte (palette index) per pixel
```

A file from a newer Fink is refused with a message rather than misread. Later: data channels
(`L<layer>_<drawing>.<channel>.bin`), atlas configs, history checkpoints.

## Points, pivots and data channels *(built)*

**Points** belong to drawings, not frames.
- A point track (the pivot, or a named point like "hand") rides on one layer, normally its line layer.
- Each drawing of that layer can place the point. A held drawing keeps it, and a reused drawing reuses it.
- A drawing that doesn't place it inherits it from the nearest frame before (then after) that does. Inherited markers show faded.
- The **pivot** is the point that stays still on screen while trimmed frames change. Without one, it's the canvas middle.
- **Point tool** (P): click places the selected point on this drawing, dragging moves it, right-click clears it here.
- **Keyed points** are the other kind. They aren't tied to drawings: they have keys on frames, and they hold or move smoothly between them, even through a held drawing.
  - Each keyed point has its own row under the layers in the timeline, with a diamond per key. Drag a diamond to retime it; right-click it to delete it.
  - With the Point tool, a click sets a key on the current frame.
  - Export splits a hold where a keyed point moves, so the game gets every position (the picture is still packed once). In the game they read like any point, `POINT_<ASSET>_<NAME>`.
- The Tools panel lists the points with where they are and whether that's inherited. Add, rename (right-click) and delete them there.
- Each point has a stable id: `POINT_<ASSET>_<NAME>` in the game.

**Data channels** are project-wide.
- They are named, typed values painted on drawings beside their colors:
  - **Value**: 0..255, e.g. emissive or a mask
  - **Normal**: a direction index into 17 directions (facing you, then 8 at 45° and 8 at 75°), exported as x/y/z
- Defaults: `normal` and `emissive`. Add more with + in the Paint section.
- Painting: choose Colors or a channel in the Tools panel's Paint section (C cycles).
  - Pencil, Line and Eraser paint values. Right-click paints 0.
  - Fill fills one shape: the clicked color's connected pixels.
  - The eyedropper picks values.
  - Data needs a drawing there, on any non-reference layer.
- The canvas shows the channel: values as gray, normals in normal-map colors, unpainted pixels dark.
- Undo works as for pixels. Duplicating a drawing copies its points and data.

**Maps** (per atlas, in the Atlas panel's Settings):
- Each map is a texture with the atlas's layout, named `<atlas>.<map>.png`.
- Each of R, G, B and A takes one of: a Value channel, a Normal channel's .x/.y/.z, or a constant.
- A channel's value at a pixel comes from the layer whose color is on top there.
- Data in A warns that the content build premultiplies alpha.
- The game gets `MAP_<NAME>` constants, and `fink atlas map(atlas, MAP_X)` returns the map's texture id.

## Asset library *(built)*

- **The Assets panel:**
  - a search box (words match any part of a name)
  - folders from "/" in names: `enemies/bat` sits in `enemies`
  - a thumbnail, size, frame count and tags for each asset
  - right-click to rename, duplicate or delete
  - thumbnails are only drawn for visible rows, so a long list stays quick
- **Above the canvas:** a slim bar with previous / next, the current asset (click it, or press **Ctrl+P**, to search and jump) and +.
- The Asset menu lists the first 15 assets.

**Saving exports:** with "Export when saving" (on by default, Atlas panel), Ctrl+S also exports every atlas that has a folder. The export runs first, because packing can move pictures and their places are saved too. An export problem is reported, and the save still happens.

**Icon:** a pixel-art F (`src/Fink.App/Assets`): `Icon.bmp` embedded for the window, `Icon.ico` for the exe.

## Atlases, indexed color and palette swaps *(built)*

Decided with the user:

- **Atlases are configurable:** a project has one or more atlas configs, each choosing which
  assets it packs, max size, power of two, padding, and its maps (below).
- **Indexed only:** atlas pixels are palette indices, not colors. A game draws them with a
  palette shader: index → color from the palette image.
- **Palette image:** `<atlas>.palette.png`, 256 wide, one row per variant (row 0 the base).
  A sprite picks its row with per-sprite data (`set sprite all texcoord1`), so swapping a palette
  is a number, not a texture. Variants are whole rows; a new one starts as a copy of the base.
- **Packing:** stable by default (cells keep their place while you work, so the live Atlas
  panel doesn't jump), with a tight repack on demand / on export. Cells are trimmed to their
  content and keep their offset, so pivots don't move. Uses `set texture frame rect`.
- **Maps are mirrored textures:** every map of an atlas has the identical layout. Which data
  goes into which texture's channels is configured per atlas:

```
atlas "characters"
  map "index"   R = palette index          A = coverage (0 / 255)        (always present)
  map "lights"  R = emissive   G = rim mask   B = 0   A = 0
  map "normal"  R = normal.x   G = normal.y   B = height   A = 0
```

  A slot is a channel component (`normal.x`), a constant, or empty. Fink's data channels are
  named, typed values painted per drawing (normal: 2 values, emissive: 1, custom labeled: 1);
  the map config is just routing. Exported with it:
  - `<atlas>.atlas.json`: which file is which map and which channel is in which slot.
  - `<atlas>.fxh`: generated shader helpers that match the routing (`float Emissive(float2 uv)`,
    `float2 Normal(float2 uv)`, `float4 PaletteColor(float2 uv, float row)`), so game shaders
    don't hard-code channel positions and can't drift from the config.
- **Rules for indexed textures** (export writes them into the json; the engine applies them):
  premultiply off, no compression, no mipmaps, point sampling; partial transparency comes
  from the palette entry's alpha, never from the texture.

### Export

```
<atlas>.png               index map          <atlas>.<map>.png   each other map (same layout)
<atlas>.palette.png       variants × 256     <atlas>.atlas.json  cells, assets, frames, tags, maps
<atlas>.fxh               shader helpers
```

`atlas.json` sketch:

```json
{
  "version": 1, "image": "characters", "size": [512, 256],
  "palette": { "image": "characters.palette.png", "variants": ["base", "red-team", "night"] },
  "maps": { "index": "characters.png", "lights": { "file": "characters.lights.png", "r": "emissive", "g": "rim" } },
  "cells": [ { "x": 0, "y": 0, "w": 21, "h": 30, "ox": 5, "oy": 2 } ],
  "assets": {
    "hero": { "size": [32, 32], "fps": 12,
              "frames": [ { "cell": 0, "ms": 167 } ],
              "tags": [ { "name": "walk", "from": 0, "to": 5, "loop": "forward" } ] }
  }
}
```

## Milestones

1. **Skeleton:** window, docking ImGui, canvas with zoom and pan, palette, pencil and eraser,
   layers with roles, frames, onion skin, undo/redo. *(done)*
   - **1b:** command system, rebindable hotkeys + Options screen, command palette, workspaces,
     UI scale, script automation with screenshots. *(done)*
   - **1c:** dope sheet: drawings, holds, reuse, fixed fps, fill following line timing,
     onion skin by drawing. *(done)*
2. **Fill + selection:** fill with gap closing across layers, selection tools, line and shape
   tools, eyedropper. *(done, apart from the rectangle/ellipse shape tools and polygon lasso)*
3. **Save/load** `.fink`, PNG import (quantized to the palette), palette import/export.
   *(projects, save/load, autosave + recovery done; imports not yet)*
4. **Export** of the atlas, `anim.json` and `.fbasic` into a game folder. **← usable for real work**
   *(atlas + anim.json + tags done; `.fbasic` not yet)*
5. **History panel** with thumbnails, checkpoints, saved brushes.
6. **Data channels** and channel painting.
7. **Shader preview.** *(done for DesktopGL: compile, hot reload, every parameter, per-sprite
   values)*
8. **`Fade.MonoGame.Anim`** in fade.mono.
9. Polish: tags and points UI, character groups, pressure, drawing thumbnails in the timeline,
   cleaning up unused drawings.
