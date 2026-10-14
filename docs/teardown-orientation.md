# Teardown: set_view_orientation (port candidate #1)

Source: `rvt-fork` dev, `src/shared/Handlers/SetViewOrientationHandler.cs`
(129 lines) + dispatcher Register + server method in `Program.cs`.
Verified live: orients + captures work; framing tuned with hacks.

## How it works now

1. Resolve 3D view (by id or active), reject templates.
2. Target = explicit `target_mm` or bbox-center of up to 50k elements
   (datums/grids/levels/sections/elevations/cameras excluded, views skipped).
3. `forward = -normalize(direction)`, `right = forward × Z`,
   `up = right × forward`. Rejects vertical direction.
4. `eye = target − forward × size × distance_factor`
   (`size` = bbox diagonal, default factor 1.5).
5. `view.SetOrientation(eye, up, forward)` in a Transaction (undoable).

## Verified problems

- **P1 framing by math, not fit.** `size` is the bbox *diagonal*: flat
  buildings → eye too far → tiny model. Live we hacked factor 0.12–1.5.
  Fix: orient, then `UIView.ZoomToFit()` (UI-level, no transaction) —
  deterministic framing at any distance. (ZoomToFit behavior to verify live.)
- **P2 bbox cost.** Per-element `get_BoundingBox(view)` for ≤50k elements
  on the UI thread. Big models → slow, risks the 60s gateway timeout.
  Fix: default to model categories (walls/floors/roofs/masses), accept
  optional `element_ids` to frame (mirrors `set_view_crop`).
- **P3 wasted scan.** Explicit `target_mm` still runs a full bbox scan
  just for `size`. Fix: explicit `distance_mm` override, skip scan.
- **P4 stringified params.** Server takes `direction_json`/`target_mm_json`
  strings (copied from `set_view_crop`). Rewrite with structured objects.
- **P5 no display-style control.** Wireframe vs shaded changes comparison.
  Optional `display_style` param (verify TX requirement live).
- **P6 untestable core.** Math inline in `Execute`; needs Revit to test.
  Rewrite: pure `FrameCalculator.Compute(target, direction, distance)`
  → (eye, up, forward), unit-tested like `BatchExecutor.Run`.
- **P7 weak errors.** Locked-view failure doesn't hint unlocking;
  empty-bbox failure doesn't suggest `element_ids`.
- **P8 confusing factor.** `distance_factor × diagonal` → replace with
  explicit `distance_mm` (default auto = 1.2 × max dimension) or fit mode.

## Rewrite shape (this repo)

```
FrameCalculator (pure, unit-tested)
  Compute(targetMm, direction, distanceMm) -> (eye, up, forward)
SetViewOrientation (thin Revit shell)
  resolve view -> target (explicit | element_ids bbox | auto cats)
  -> TX SetOrientation -> ZoomToFit -> return eye/target/size/zoomed
MCP: structured params {view_id?, direction?, target_mm?,
  element_ids?, distance_mm?, display_style?}
Tests: math cases + shell serialization. NO hardcoded tool counts here.
```

## Assumptions carried over (need section data)

Wall heights 3600, stock thicknesses — from the church build, not from
this tool. Unrelated to the rewrite.
