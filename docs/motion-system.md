# ClipShelf Windows motion system

This document describes the internal motion contract. It is not a promise of a
fixed frame rate: all trajectories use actual elapsed time, and rendering
callback count is never treated as the display refresh rate.

## Layers

- `MotionTokens` contains semantic profiles for hover, press, selection,
  indicators, overlays, sheets, previews, list layout, insertion, removal and
  direct-manipulation release. UI code selects intent instead of inventing a
  duration or easing curve.
- `MotionValue` owns one scalar spring state. Retargeting preserves its current
  presented value and velocity; long dispatcher gaps are bounded and converge
  safely.
- `MotionDriver` is the single WPF render-loop owner. A channel is identified by
  dependency-object owner and semantic name. There is at most one source for a
  property, and channels are cancellable, redirectable and removed when their
  owner unloads or becomes invisible.
- `PressableMotion` adds pointer and keyboard feedback without replacing button
  commands, hit testing, focus visuals, tooltips or automation names.
- `VisibleRowLayoutTransition` captures only realized `ListBoxItem` containers.
  Stable record IDs provide FLIP offsets; it never enumerates or generates the
  whole history visual tree. Visible deletion snapshots are in-memory only and
  are released after their exit finishes.

`WheelScrollMotion` reuses `MotionValue`, but the existing wheel target,
diagnostics and response presets remain authoritative. Trackpad and scrollbar
dragging stay native. Selection dragging remains direct and has no fabricated
inertia because WPF does not provide a trustworthy release velocity for that
gesture.

## Lifecycle and accessibility

Every temporary owner cancels its channels on unload/close. Main-window quit
cancels the driver globally after preview cleanup. Settings and preview exits
remain visible until their visual exit completes, and a new open request
reverses the same live channels rather than creating another storyboard.

`MotionPolicy` disables spatial motion when Windows client-area animation is
off or high contrast is active. Controls still expose static selected, pressed,
focus and automation states; business completion never waits for an invisible
animation.

## Diagnostic switches

- `--motion-micro=off`
- `--motion-indicator=off`
- `--motion-overlay=off`
- `--motion-list=off`
- `--motion-direct=off`
- `--motion=off` (all domains)

The switches are runtime diagnostics only and do not change persisted settings
or history formats.

## Adding motion

1. Choose an existing semantic token. Add a token only when the interaction has
   a genuinely different physical role.
2. Animate transform or opacity where possible; do not animate list height,
   margin, text layout or other properties that cause full-tree measure work.
3. Use a stable owner/channel pair, apply the current presented value, and
   cancel on unload or close.
4. Provide a static reduced-motion/high-contrast result and preserve keyboard,
   focus and automation behavior.
5. Add elapsed-time, interruption and cleanup regression coverage. Visual feel
   still requires an isolated manual run; unit tests are not FPS evidence.

## Reference boundary

The TieZ clipboard project was inspected only for general interaction ideas such
as shared indicators, grouped presence changes and semantic visual tokens. It is
GPL-3.0 and uses a different React/Tauri stack. No TieZ source code, CSS,
assets, icons, copy or identifiable implementation was copied, translated or
adapted into ClipShelf; this WPF implementation is independent.
