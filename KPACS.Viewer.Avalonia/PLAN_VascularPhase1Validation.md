# Phase 1 Vascular Validation and Performance Guardrails

## Scope

This checklist targets the Phase 1 aorto-iliac CTA workflow:
- 3D vessel mask creation and edit
- centerline extraction
- orthogonal vessel cross-sections
- curved MPR / CPR
- EVAR neck and distal landing-zone baseline measurements

## Dataset checklist

Use at least one representative case from each group:
- standard abdominal aorto-iliac CTA with good contrast and full renal-to-iliac coverage
- aneurysm case with wide neck or thrombus-adjacent lumen
- tortuous iliac continuation with strong centerline curvature
- heavily calcified case
- case with streak / metal artifact if available
- incomplete or marginal coverage case to verify safe failure behavior

For each validation case confirm:
- contrast enhancement is adequate for lumen-oriented review
- coverage extends from the proximal neck region through distal landing zones
- slice thickness and reconstructed spacing are suitable for isotropic or near-isotropic review
- no stale mask, centerline, or planning session state was restored from another geometry

## Manual geometry checklist

Review and mark each item as `Pass`, `Fail`, or `Pending`:
- centerline stays inside the lumen in neck, aneurysm body, and iliac bends
- orthogonal cross-sections remain visually perpendicular and stable during scrubbing
- curved MPR station matches the same location as the orthogonal cross-section
- EVAR neck markers produce plausible neck length and equivalent diameter values
- distal landing markers produce plausible distal length and equivalent diameter values
- recalculation after mask edits updates centerline and metrics without obvious drift

## Performance budgets

Targets for Phase 1 demo/release readiness:
- mask edit commit: <= 250 ms
- centerline calculation: <= 1500 ms
- orthogonal cross-section scrub: <= 120 ms
- curved MPR update: <= 250 ms

Interpretation:
- one-off spikes can happen during warm-up
- repeated over-budget updates on standard CTA cases should be treated as regressions
- restore-from-session should not cause the planning workspace to hang or visibly stall

## Known failure modes

Document and review before release:
- low contrast or delayed enhancement can pull the centerline toward the wall
- calcified or thrombus-adjacent lumen can bias equivalent diameter estimates
- metal artifacts can distort both CPR continuity and orthogonal appearance
- incomplete renal-to-iliac coverage invalidates landing-zone planning
- highly tortuous or branching anatomy may require guide seeds and manual marker review

## Release note

Phase 1 validation is intentionally lightweight and manual. It is meant to catch obvious clinical-demo regressions early, not to replace a regulatory verification package.
