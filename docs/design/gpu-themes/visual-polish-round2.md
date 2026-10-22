# GPU themes visual polish round 2

> Date: 2026-10-21  
> Execution model: DeepSeek V4.1 Flash  
> Status: candidates ready; G2 still requires separate user approval

## User feedback addressed

The overall design direction and display stability were accepted. The visual
expression was judged slightly underdeveloped, so the fixed-input candidates
were revised rather than treated as approved.

## Liquid Glass changes

- Rebuilt the sample into four balanced optical groups: primary metric, live
  telemetry, GPU thermal package, and sensor fabric.
- Added a double-layer rim treatment with cool top light, warm counter-light,
  an inner lip, a directional cast shadow, and a restrained moving caustic.
- Added background luminance adaptation so bright and saturated regions are
  locally settled without flattening the whole material.
- Added clear progress rails, separators, and status marks. Readability on
  bright regions is handled by the shader's local luminance response; no
  rectangular scrim is drawn inside the rounded glass.
- Preserved unresolved sensor and warning states while keeping text outside
  refraction and animation.

## Cyberpunk changes

- Added outer/inner mechanical framing, segmented cyan header rails, corner
  accents, screw wells, vents, brushed-metal variation, and restrained glow.
- Reworked the main panel into CPU, GPU, memory, network, and a four-GPU thermal
  bank with independent online, offline, and warning states.
- Added channel-specific network and thermal cards rather than repeating two
  similar boxes.
- Made progress rails segmented with a bright leading edge and a faint local
  scanner. Static hierarchy remains complete without motion.

## Rendering issues found and fixed

- The glass meter signed-distance test treated the exterior as inside; this
  produced full-surface highlight blocks during the first revision.
- `Vortice.Mathematics.Rect` receives logical width and height. Treating the
  supplied width/height as right/bottom coordinates enlarged Direct2D fills
  and text layouts; the foreground helpers now use the correct convention.
- The foreground render target is explicitly cleared and the D3D context is
  flushed after a foreground update.
- The first polished glass revision still showed frame-in-frame layering. The
  causes were an inset rectangular scrim plus a separate inner highlight and
  refraction contour. The scrim was removed, the highlight peaks were merged,
  and refraction was reduced from 2.5 to 1.6 logical pixels.
- The dedicated capture backdrop is now topmost and the lab is brought above
  it before capture. An earlier failed live attempt exposed an unrelated
  foreground window; its generated pixels were overwritten with blank
  placeholders and the `v2` capture is the only retained polished live result.

## Current candidates

| Theme | File | SHA-256 |
|---|---|---|
| Liquid Glass | `R06/glass-sample/fixed-glass-v16.png` | `CA9689A2C02166AD93FD3F7354B9A49BF42BE669A9F311A8DFBF6465D82C0752` |
| Cyberpunk | `R07/cyber-sample/fixed-cyber-v3.png` | `EBE521EAFE2C883F54220DEDBFCD7C473349973F1FC9429C010C01752E2EAABF` |

Live joint evidence:

| Artifact | SHA-256 |
|---|---|
| `R04/joint/live-glass-polished-excluded-v3.png` | `12523E03FD3B3D5952450601EF61F13BD84BA359A07948F7C9FD83C2DE030371` |
| `R04/joint/external-capture-polished-excluded-v3.png` | `27079DDD6DA7D9FA4E01B115C0E0499A6F557BBDF400975379F385BC3F8EE0ED` |

Evidence root: `docs/reviews/evidence/gpu-themes/694a8c5/`.

## Verification

- Shader compilation: pass.
- Prototype Release build: 0 warnings, 0 errors.
- Production Release build: 0 warnings, 0 errors.
- Rendering contract tests: 4 passed, 0 failed.
- Full main test suite: 651 passed, 7 skipped, 0 failed.
- Live glass: `FrameCopied`, WDA active (`0x00000011`), dedicated synthetic
  backdrop confirmed by simultaneous external capture.

## Open gates

- Liquid Glass v16 still needs separate user visual approval.
- Cyberpunk v3 still needs separate user visual approval.
- The product decision for making the entire application invisible to
  supported system screenshots and recordings remains unresolved.
