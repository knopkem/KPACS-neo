# Phase 1 GitHub Issue Set - Vascular EVAR MVP

## Purpose

This document turns the vascular workstation roadmap into an actionable first delivery backlog.

Scope of Phase 1:

- editable 3D vessel mask foundation
- aorto-iliac centerline MVP
- orthogonal vessel cross-sections
- first curved MPR path
- EVAR neck and landing-zone baseline measurements
- planning session persistence

Constraint:

- stay on OpenCL 2.1
- build on the current ROI and volume-rendering architecture
- do not attempt TAVI, bypass, endoscopy, or full stenosis package in this phase

## Recommended implementation order

1. domain model for 3D masks
2. convert current auto-ROI output into editable masks
3. 3D mask editing operations with undo/redo
4. centerline data model and seed workflow
5. aorto-iliac centerline prototype
6. orthogonal cross-section viewport
7. first curved MPR viewport
8. EVAR neck and landing-zone measurement bundle
9. planning session persistence
10. validation and performance guardrails

---

## Issue 1 - Add SegmentationMask3D domain model and persistence hooks

**Type**
- Epic starter / foundational infrastructure

**Problem**
- The current 3D ROI workflow is primarily contour-centric.
- EVAR planning needs a stable, editable 3D representation that can be reused by centerline extraction, reslicing, measurement, and reporting.

**Goal**
- Introduce a first-class 3D segmentation object that remains aligned with the source volume and can be persisted with study state.

**In scope**
- Add `SegmentationMask3D` model
- Store dimensions, spacing, orientation, origin, and source-volume identity
- Support binary mask storage for first version
- Add serialization/deserialization support for viewer study state
- Add provenance metadata such as creation source (`auto-roi`, `manual-edit`, `imported`)

**Out of scope**
- multi-label segmentation
- DICOM SEG export
- advanced compression of mask storage

**Suggested files / areas**
- `KPACS.Viewer.Avalonia/Models/`
- `KPACS.Viewer.Avalonia/Services/`
- measurement persistence code paths

**Dependencies**
- none

**Definition of done**
- A `SegmentationMask3D` instance can be created from a loaded volume
- Spatial metadata round-trips correctly after serialization
- Reopening a saved study reconstructs the same mask extents and alignment
- No new build warnings or errors are introduced

**Notes**
- Keep API small and explicit
- Prefer correctness and coordinate safety over premature optimization

---

## Issue 2 - Convert current 3D auto-ROI output into editable mask storage

**Type**
- feature infrastructure

**Problem**
- Existing auto-ROI output is useful for preview and contour generation, but not yet the durable backbone for vascular planning.

**Goal**
- Turn the existing 3D auto-outline result into a real mask object that can be edited and reused.

**In scope**
- Add conversion path from current `VolumeRoi` or contour output to `SegmentationMask3D`
- Preserve current user-facing auto-ROI workflow where practical
- Attach generated mask to current study/viewport context
- Keep preview functionality working

**Out of scope**
- redesign of full ROI UI
- branch-aware vessel segmentation

**Suggested files / areas**
- `KPACS.Viewer.Avalonia/Controls/DicomViewPanel.AutoOutline.cs`
- `KPACS.Viewer.Avalonia/Models/`
- `KPACS.Viewer.Avalonia/StudyViewerWindow.VolumeRoiDraftPanel.cs`

**Dependencies**
- Issue 1

**Definition of done**
- Auto 3D ROI can produce an attached `SegmentationMask3D`
- Mask can be visualized and referenced after ROI draft completion
- Existing 3D ROI preview remains functional
- A failed conversion produces a safe user-facing error without corrupting study state

---

## Issue 3 - Implement additive and subtractive 3D mask editing with undo/redo

**Type**
- core workflow feature

**Problem**
- Vascular masks will fail at thrombus borders, calcified regions, bifurcations, and wall bridges unless users can repair them quickly.

**Goal**
- Let the user refine the mask with clinically useful local edits.

**In scope**
- Add brush/ball-based add and erase operations for mask voxels
- Add remove-island operation for disconnected fragments
- Reuse current ROI correction concepts where practical
- Add undo/redo support for mask edits
- Update live preview after edit commit

**Out of scope**
- full sculpting toolkit
- spline-based volumetric cutting
- AI segmentation correction

**Suggested files / areas**
- `KPACS.Viewer.Avalonia/Controls/DicomViewPanel.RoiBallCorrection.cs`
- `KPACS.Viewer.Avalonia/StudyViewerWindow.VolumeRoiDraftPanel.cs`
- new mask editing service(s)

**Dependencies**
- Issue 2

**Definition of done**
- User can add to a mask locally
- User can subtract from a mask locally
- User can undo and redo mask edits reliably
- Mask and preview remain spatially aligned after edits
- UI remains responsive during typical edit sizes

**Notes**
- Start with commit-on-release interaction if continuous editing is too heavy for the first pass

---

## Issue 4 - Add centerline path domain model and seed placement workflow

**Type**
- domain + UI foundation

**Problem**
- CPR, orthogonal cross-sections, and landing-zone metrics require a reusable centerline object and deterministic user input.

**Goal**
- Introduce the model and the minimum UX needed to compute and correct an aorto-iliac centerline.

**In scope**
- Add `CenterlinePath` model with ordered points, arc length, and metadata
- Add seed placement workflow for proximal and distal endpoints
- Add optional intermediate guide points for correction
- Add persistent storage of seeds with planning state

**Out of scope**
- full branch tree management
- automatic vessel labeling

**Suggested files / areas**
- `KPACS.Viewer.Avalonia/Models/`
- `KPACS.Viewer.Avalonia/StudyViewerWindow.*`
- viewer interaction code paths

**Dependencies**
- Issue 1 minimally helpful
- Issue 2 or 3 preferred

**Definition of done**
- User can place start and end points for a centerline request
- Path model can store and reload the chosen seed set
- Basic UI state exists for entering and leaving centerline edit mode

---

## Issue 5 - Prototype aorto-iliac centerline extraction from the segmentation mask

**Type**
- core algorithm feature

**Problem**
- Without a robust centerline, vascular planning remains a generic 3D viewing exercise.

**Goal**
- Compute a clinically usable first-pass centerline through the aorto-iliac lumen.

**In scope**
- Build a mask-based centerline prototype focused on abdominal aorta and iliac continuation
- Add smoothing and resampling of output path
- Handle common aneurysm and iliac tortuosity cases reasonably
- Add quality/failure status result

**Out of scope**
- full branch tree extraction
- renal / mesenteric branch automation
- carotid or coronary workflows

**Suggested files / areas**
- new centerline service in `Services/` or `Models/`
- planning workflow orchestration in `StudyViewerWindow`

**Dependencies**
- Issue 3 recommended
- Issue 4 required

**Definition of done**
- Prototype computes a centerline for standard aorto-iliac CTA cases
- Output is stored as `CenterlinePath`
- Failures are surfaced cleanly with actionable status
- Re-run after mask edits is supported

**Notes**
- Clinical reliability matters more than algorithmic elegance
- CPU implementation is acceptable for the first pass if interaction remains usable

---

## Issue 6 - Add orthogonal vessel cross-section viewport bound to centerline position

**Type**
- workflow-enabling UI feature

**Problem**
- Landing-zone and lumen measurements need consistent perpendicular cross-sections.

**Goal**
- Show a true centerline-orthogonal cross-section and synchronize it with the active path position.

**In scope**
- Add viewport or panel for orthogonal vessel slice
- Bind current sampling plane to centerline position
- Support scrubbing along path length
- Show crosshair sync to native views where practical

**Out of scope**
- automatic lumen contour extraction
- advanced rotational measurements

**Suggested files / areas**
- `KPACS.Viewer.Avalonia/StudyViewerWindow.RenderingWorkspace.cs`
- `KPACS.Viewer.Avalonia/Controls/`
- `KPACS.Viewer.Avalonia/Rendering/VolumeComputeBackend.cs`

**Dependencies**
- Issue 5

**Definition of done**
- User can move along the centerline and see the perpendicular vessel slice update
- Slice orientation remains stable through bends
- Performance is acceptable on standard CTA datasets

---

## Issue 7 - Add first curved MPR rendering path using existing volume backend

**Type**
- major workflow feature

**Problem**
- Endovascular planning needs a vessel-following longitudinal view, not just axial/MPR/DVR.

**Goal**
- Deliver a first curved MPR implementation that reuses current rendering infrastructure where possible.

**In scope**
- Add curved MPR viewport bound to centerline path
- Support path scrubbing synchronization with orthogonal cross-section
- Use current volume loading and rendering backend where possible
- Add minimal controls for slab/width if cheap to support

**Out of scope**
- stretched MPR
- tangential view
- endoscopy

**Suggested files / areas**
- `KPACS.Viewer.Avalonia/Rendering/VolumeComputeBackend.cs`
- `KPACS.Viewer.Avalonia/StudyViewerWindow.RenderingWorkspace.cs`
- new curved-MPR-specific rendering helpers

**Dependencies**
- Issue 5
- Issue 6 strongly recommended

**Definition of done**
- User can inspect a curved MPR along the generated centerline
- Curved MPR remains synchronized with centerline position and orthogonal slice
- Rendering path handles cancellation safely and does not block UI thread

---

## Issue 8 - Implement EVAR neck and landing-zone baseline measurement bundle

**Type**
- clinical planning feature

**Problem**
- The MVP is only valuable clinically if it produces planning-ready core measurements.

**Goal**
- Provide a first persistent set of EVAR metrics tied to centerline and cross-sections.

**In scope**
- Proximal neck length
- neck diameter sampling over a selected span
- neck angulation baseline
- landing-zone start/end markers
- distal landing-zone measurements for iliac limbs
- summary bundle attached to study state

**Out of scope**
- full endograft recommendation engine
- branch ostia distances beyond minimal placeholders
- TEVAR thoracic specifics

**Suggested files / areas**
- `KPACS.Viewer.Avalonia/StudyViewerWindow.ReportPanel.cs`
- `KPACS.Viewer.Avalonia/StudyViewerWindow.MeasurementInsights.cs`
- new vascular planning measurement models

**Dependencies**
- Issue 6
- Issue 7 helpful

**Definition of done**
- User can mark or confirm proximal and distal landing spans
- Baseline EVAR measurements are persisted and shown in a structured bundle
- Measurements can be regenerated after centerline correction

---

## Issue 9 - Persist vascular planning sessions in study state

**Type**
- infrastructure / workflow completion

**Problem**
- Planning work loses value if masks, centerlines, and measurements cannot be resumed reproducibly.

**Goal**
- Save and reopen a complete Phase 1 planning session.

**In scope**
- Persist segmentation mask reference/state
- persist centerline seeds and resolved path
- persist active landing-zone markers and measurement bundle
- persist basic viewport/workspace state needed to resume planning

**Out of scope**
- cross-user collaboration
- external report export formats

**Suggested files / areas**
- study state serialization code
- planning models under `Models/`
- relevant `StudyViewerWindow` state logic

**Dependencies**
- Issues 1, 4, 8

**Definition of done**
- Reopening the study restores the planning session without critical drift
- Stored session is resilient to partial missing state and fails safely
- Restored session does not expose stale or mismatched geometry

---

## Issue 10 - Add validation dataset checklist and performance guardrails

**Type**
- release-hardening task

**Problem**
- Interactive planning features will regress quickly without explicit dataset and latency targets.

**Goal**
- Define a minimum validation framework for the Phase 1 release.

**In scope**
- Create dataset checklist for aorto-iliac CTA cases
- Define target latency budgets for:
  - mask edit commit
  - centerline calculation
  - orthogonal cross-section scrubbing
  - curved MPR update
- Add manual verification checklist for geometry correctness
- Document known failure modes

**Out of scope**
- fully automated clinical validation suite
- regulatory-grade verification package

**Suggested files / areas**
- planning docs
- test dataset notes
- optional future validation helpers

**Dependencies**
- none, but should be updated as Issues 1 to 9 progress

**Definition of done**
- Team has explicit Phase 1 validation checklist
- Performance expectations are written down and reviewable
- Known-risk scenarios are documented before release

---

## Suggested milestone grouping

### Sprint / Milestone 1
- Issue 1
- Issue 2
- Issue 3

### Sprint / Milestone 2
- Issue 4
- Issue 5

### Sprint / Milestone 3
- Issue 6
- Issue 7

### Sprint / Milestone 4
- Issue 8
- Issue 9
- Issue 10

## Suggested labels

- `vascular`
- `phase-1`
- `evar`
- `planning`
- `imaging-core`
- `rendering`
- `measurements`
- `persistence`
- `validation`

## Fastest path to visible clinical value

If the team wants the shortest route to a meaningful demo, prioritize:

1. Issue 2
2. Issue 3
3. Issue 4
4. Issue 5
5. Issue 6
6. Issue 8

That sequence yields:
- editable vessel mask
- centerline prototype
- perpendicular vessel views
- first EVAR planning metrics

Curved MPR then becomes the major visual upgrade rather than the blocker for the whole MVP.
