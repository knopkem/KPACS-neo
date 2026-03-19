# Vascular Workstation Roadmap

## Goal

Build a dedicated vascular planning workstation on top of the existing KPACS Avalonia viewer for:

- stenosis quantification
- bypass planning
- EVAR / TEVAR planning
- TAVI planning
- future endograft design support based on 3D auto-ROI

Constraint: stay on OpenCL 2.1 and evolve the current viewer architecture rather than replacing it.

## Current foundation in the codebase

Existing building blocks already reduce implementation risk:

- 3D auto-ROI draft generation and region-growth style logic in `Controls/DicomViewPanel.AutoOutline.cs`
- 3D ROI draft preview and additive workflow in `StudyViewerWindow.VolumeRoiDraftPanel.cs`
- local ROI cleanup tooling in `Controls/DicomViewPanel.RoiBallCorrection.cs`
- contour interpolation and mesh-slice blending in `Models/VolumeRoiInterpolationHelper.cs`
- OpenCL-backed volume rendering and MPR/DVR kernels in `Rendering/VolumeComputeBackend.cs`
- anatomy/report infrastructure with early centerline-like axis models in `StudyViewerWindow.AnatomyWorkspace.cs` and `StudyViewerWindow.ReportPanel.cs`

## Product pillars

1. **Segmentation**
   - robust vessel and lumen isolation
   - editable 3D masks for procedural planning
   - reusable segmentation for all downstream workflows

2. **Centerline and vessel views**
   - automatic centerline extraction
   - curved MPR / stretched MPR
   - orthogonal vessel cross-sections
   - tangential and future endoscopy-style views

3. **Quantification**
   - diameter, area, stenosis percent, lesion length
   - neck geometry, landing zones, tortuosity, access routes
   - TAVI annulus and coronary metrics

4. **Procedure planning**
   - EVAR / TEVAR sizing and landing-zone planning
   - TAVI annulus and access planning
   - bypass route planning
   - future endograft design support

5. **Structured reporting**
   - persistent measurement bundles
   - reproducible screenshots and planning sheets
   - exportable plan summaries

## Technical direction

### Core model changes

Introduce stable planning primitives instead of passing raw ROI contours through all workflows:

- `SegmentationMask3D`
- `VesselTreeModel`
- `CenterlinePath`
- `CrossSectionFrame`
- `StenosisMeasurement`
- `LandingZoneMeasurement`
- `AccessRouteAssessment`
- `TaviAnnulusModel`
- `EndograftPlanModel`

### Compute strategy

Keep OpenCL 2.1 and focus on the data flow:

- keep volume, mask, distance-map, and reslice buffers GPU-resident where practical
- use OpenCL for mask morphology, fast resampling, cross-section sampling, and preview rendering
- keep graph optimization / pathfinding hybrid where CPU algorithms are easier to validate clinically
- avoid repeated host-device copies during interactive planning

### UI strategy

Add a dedicated vascular workspace with synchronized views:

- axial / sagittal / coronal reference viewports
- DVR / MIP vascular view
- curved MPR viewport
- orthogonal cross-section viewport
- planning panel with stepwise workflow state

## Delivery plan

---

## Epic 1 - Convert 3D ROI into a true editable segmentation mask

**Why first**
This is the base for EVAR design, centerlines, vessel measurements, and all procedural planning.

**Outcome**
User can create, refine, save, reload, and reuse a 3D vessel mask instead of only a contour-derived ROI draft.

### Stories

#### 1.1 Add persistent 3D mask model
- Create a voxel/labelmap representation for 3D ROI output
- Preserve spatial metadata, orientation, spacing, and provenance
- Support serialization in study state

**Acceptance criteria**
- A 3D ROI can be saved and restored without geometry drift
- Mask aligns exactly with source volume coordinates
- Mask survives reopen of the study session

#### 1.2 Add mask edit operations
- Add additive, subtractive, erase, and island cleanup operations
- Reuse current ball correction UX where possible
- Support slice-based and 3D-local edits

**Acceptance criteria**
- User can remove unwanted wall bridges and leak regions
- User can merge disconnected lumen parts intentionally
- Undo/redo works for all edit actions

#### 1.3 Add mask quality tools
- Fill holes
- remove tiny islands
- smooth jagged borders
- preserve branch ostia when smoothing

**Acceptance criteria**
- Quality tools improve mask usability without destroying branch anatomy
- Results are previewable before commit

#### 1.4 Add segmentation state panel
- Show mask volume, voxel count, bounding box, and basic confidence indicators
- Expose edit history and active region statistics

**Acceptance criteria**
- User can inspect segmentation quality without leaving the workspace

**Dependencies**
- existing 3D auto-ROI and ROI correction code

**Priority**
- P0

---

## Epic 2 - Automatic vessel centerline extraction and correction

**Why second**
Centerline is the backbone for stenosis analysis, curved MPR, landing-zone measurement, and access planning.

**Outcome**
User can generate and correct centerlines for aorta, iliacs, and later branch vessels.

### Stories

#### 2.1 Build centerline seed workflow
- Allow seed placement for proximal and distal points
- Add optional branch inclusion / exclusion hints
- Auto-suggest seeds from segmentation geometry

**Acceptance criteria**
- User can create a centerline with 2-3 clicks in common aorto-iliac cases

#### 2.2 Implement centerline extraction core
- Distance-map based center estimation
- path optimization through lumen
- smoothing and resampling for stable downstream views

**Acceptance criteria**
- Centerline stays inside lumen in standard CTA cases
- Centerline remains stable across aneurysm neck and iliac bends

#### 2.3 Add manual centerline correction
- drag nodes
- insert correction points
- lock segments
- split or merge paths

**Acceptance criteria**
- User can correct failed sections without recomputing the full path

#### 2.4 Add vessel-tree data structure
- model trunk and branches
- mark named segments (aorta, common iliac, external iliac, etc.)
- support future renal, mesenteric, and coronary branches

**Acceptance criteria**
- Centerline output is reusable by CPR, quantification, and reports

**Dependencies**
- Epic 1 completed or at least minimally available with reliable mask output

**Priority**
- P0

---

## Epic 3 - Curved MPR, stretched MPR, and orthogonal vessel cross-sections

**Why third**
This unlocks actual vascular planning workflows rather than generic 3D viewing.

**Outcome**
User can inspect vessels along the lumen path with reproducible measurements.

### Stories

#### 3.1 Add curved MPR viewport
- Reslice along centerline
- support slab thickness, width, inversion, and presets
- synchronize cursor with 3D and axial views

**Acceptance criteria**
- Curved MPR follows full centerline with acceptable latency during interaction

#### 3.2 Add stretched MPR mode
- Long vessel axis shown in straightened representation
- consistent mapping between stretched and native positions

**Acceptance criteria**
- User can inspect long lesions and access routes in one continuous view

#### 3.3 Add orthogonal cross-section viewport
- perpendicular slice at current centerline position
- rotate around vessel axis when needed
- expose lumen diameter and area overlays

**Acceptance criteria**
- Cross-section updates interactively when scrubbing along centerline
- Diameter and area overlays align with lumen contour

#### 3.4 Add tangential view
- derive local vessel frame from centerline
- show tangent-aligned inspection plane

**Acceptance criteria**
- Tangential view stays stable through bends and tortuous segments

**Dependencies**
- Epic 2 centerline output
- GPU reslice path on current OpenCL backend

**Priority**
- P0

---

## Epic 4 - Stenosis quantification workflow

**Why now**
This creates immediate clinical value for carotid, peripheral, aorto-iliac, and graft-related assessment.

**Outcome**
User can compute reproducible stenosis measurements tied to a vessel path.

### Stories

#### 4.1 Add lumen contour extraction per cross-section
- derive lumen contour from segmentation or local intensity profile
- allow manual contour correction

**Acceptance criteria**
- Cross-section contour can be edited if automation fails

#### 4.2 Add diameter and area metrics
- minimum diameter
- maximum diameter
- area
- equivalent circular diameter
- eccentricity

**Acceptance criteria**
- Metrics update live at current centerline location

#### 4.3 Add stenosis reference logic
- choose proximal/distal reference segments
- compute NASCET-style and area-based percent stenosis where applicable

**Acceptance criteria**
- User can freeze a lesion segment and a reference segment and obtain reproducible stenosis values

#### 4.4 Add lesion bundle reporting
- lesion length
- worst point
- reference segment
- screenshots and key measurements

**Acceptance criteria**
- Stenosis package exports into report-ready summary

**Dependencies**
- Epic 3

**Priority**
- P1

---

## Epic 5 - EVAR / TEVAR planning workspace

**Why this is the main vascular planning deliverable**
This is the path from current 3D auto-ROI toward a true endovascular planning product.

**Outcome**
User can segment aneurysm anatomy, assess landing zones, and produce device-sizing relevant measurements.

### Stories

#### 5.1 Add aortic planning protocol
- dedicated workflow state for abdominal / thoracic aorta
- anatomy presets and planning checklist

**Acceptance criteria**
- User can enter an EVAR or TEVAR workflow from one command and see relevant panels only

#### 5.2 Add aneurysm and neck characterization
- proximal neck diameter
- neck length
- neck conicity
- neck angulation
- maximal sac diameter
- thrombus burden markers

**Acceptance criteria**
- Core neck and sac metrics can be measured and persisted in one planning bundle

#### 5.3 Add landing-zone tools
- define proximal and distal landing segments on centerline
- compute diameters, lengths, and taper across selected spans

**Acceptance criteria**
- Landing-zone measurements remain linked to anatomy after user corrections

#### 5.4 Add branch ostia distance measurements
- renal, SMA, celiac, hypogastric, left subclavian, etc.
- measure centerline distances from planned seal zones

**Acceptance criteria**
- Distances are exportable and shown in a structured plan summary

#### 5.5 Add access-route assessment
- iliac / femoral minimum diameter
- calcification burden flag hooks
- tortuosity and narrowest-path summary

**Acceptance criteria**
- User receives a route suitability summary for access planning

#### 5.6 Add endograft design groundwork
- represent candidate graft body and limbs abstractly
- attach planned seal zones and target diameters
- keep model generic for future custom EVAR design

**Acceptance criteria**
- Planning model can store abstract graft geometry inputs even before visual device modeling exists

**Dependencies**
- Epics 1 to 3 minimum
- Epic 4 useful but not mandatory for first EVAR release

**Priority**
- P0/P1

---

## Epic 6 - TAVI planning workspace

**Outcome**
User can perform annulus and access measurements required for structural-heart planning.

### Stories

#### 6.1 Add annulus plane definition
- auto-suggest annulus plane from landmarks
- manual landmark override

**Acceptance criteria**
- Annulus plane can be reviewed and adjusted explicitly

#### 6.2 Add annulus metrics
- min/max diameter
- area
- perimeter
- derived equivalent diameter

**Acceptance criteria**
- Measurements are stable and preserved with plan state

#### 6.3 Add root and coronary metrics
- sinus of Valsalva diameters
- STJ diameter
- coronary ostial heights

**Acceptance criteria**
- Structured export includes all core TAVI sizing values

#### 6.4 Add transfemoral access assessment reuse
- share route analysis with EVAR module where possible

**Acceptance criteria**
- TAVI workflow reuses common access pipeline instead of duplicating logic

**Dependencies**
- centerline / cross-section framework

**Priority**
- P1

---

## Epic 7 - Bypass planning workflow

**Outcome**
User can evaluate inflow/outflow targets and route characteristics for surgical planning support.

### Stories

#### 7.1 Add target vessel selection and labeling
- choose inflow and outflow segments
- annotate disease-free landing stretches

**Acceptance criteria**
- Selected vessel targets remain attached to anatomy and report bundle

#### 7.2 Add route-length and diameter summary
- compute path length and target diameters
- mark hostile or narrowed segments

**Acceptance criteria**
- User gets a concise graft route summary from chosen targets

#### 7.3 Add runoff and branch review checklist
- structured, semi-quantitative planning notes

**Acceptance criteria**
- Workflow produces a reusable bypass planning summary

**Dependencies**
- centerline / vessel tree / quantification base

**Priority**
- P2

---

## Epic 8 - Reporting, persistence, and plan bundles

**Outcome**
All planning artifacts become persistent, reviewable, and exportable.

### Stories

#### 8.1 Add vascular planning session model
- save masks, centerlines, views, measurements, and selected landmarks

**Acceptance criteria**
- Planning session can be reopened and reviewed reproducibly

#### 8.2 Add structured report sections
- stenosis report block
- EVAR / TEVAR block
- TAVI block
- bypass block

**Acceptance criteria**
- Each workflow exports a concise standardized summary

#### 8.3 Add key image and screenshot capture
- curved MPR snapshot
- orthogonal cross-section snapshot
- DVR planning view snapshot

**Acceptance criteria**
- Export contains clinically relevant visual evidence for main measurements

**Dependencies**
- can begin early, but final integration depends on all clinical modules

**Priority**
- P1

---

## Epic 9 - Validation and clinical hardening

**Outcome**
Features are robust enough for repeated workstation use and future regulatory hardening.

### Stories

#### 9.1 Add deterministic test datasets
- CTA aorta with aneurysm
- iliac stenosis case
- carotid stenosis case
- TAVI root case

#### 9.2 Add measurement repeatability checks
- ensure same case yields stable metrics across sessions and edits

#### 9.3 Add failure-mode handling
- low contrast
- calcified segments
- thrombus-adjacent lumen
- metallic artifacts
- incomplete coverage

#### 9.4 Add performance budgets
- centerline generation target
- curved MPR interaction target
- mask edit latency target

**Acceptance criteria**
- Each major workflow has known edge-case behavior and performance envelope

**Priority**
- P0 for release readiness, even if implemented alongside feature work

---

## Recommended milestones

### Milestone A - Vascular MVP
Deliver:
- Epic 1 minimum
- Epic 2 minimum
- Epic 3 minimum
- Epic 5 partial (basic EVAR workflow)

Use case supported:
- aorto-iliac segmentation
- centerline extraction
- curved MPR
- orthogonal cross-sections
- neck and landing-zone measurements

### Milestone B - Quantification release
Deliver:
- Epic 4 complete
- Epic 8 partial
- Epic 9 partial

Use case supported:
- reproducible stenosis assessment and vessel measurement package

### Milestone C - Endovascular planning release
Deliver:
- Epic 5 complete
- Epic 6 partial or complete
- Epic 8 complete
- Epic 9 expanded

Use case supported:
- EVAR / TEVAR planning with structured outputs
- TAVI core sizing if prioritized

### Milestone D - Advanced workstation release
Deliver:
- Epic 6 complete
- Epic 7 complete
- tangential and optional endoscopy extensions
- future custom graft design groundwork

## Recommended first GitHub issues

1. **Create `SegmentationMask3D` domain model and persistence hooks**
2. **Convert current 3D auto-ROI output into editable mask storage**
3. **Implement additive/subtractive 3D mask editing with undo/redo**
4. **Add centerline seed UX and vessel path data model**
5. **Prototype aorto-iliac centerline extraction from existing 3D ROI**
6. **Add orthogonal cross-section viewport bound to centerline position**
7. **Add first curved MPR rendering path reusing current volume backend**
8. **Implement EVAR neck and landing-zone measurement bundle**
9. **Persist vascular planning sessions in study state**
10. **Create validation dataset checklist and performance targets**

## Suggested ownership split

- **Imaging / compute**: masks, OpenCL kernels, reslice, performance
- **Geometry / analysis**: centerline, cross-sections, metrics, landing zones
- **Workflow / UI**: vascular workspace, step panels, interaction design
- **Reporting / persistence**: plan bundles, exports, study integration
- **Validation**: datasets, repeatability, failure analysis

## Biggest risks

- current ROI representation may remain too contour-centric unless a true mask model is introduced early
- centerline quality can degrade badly in aneurysm, thrombus, and calcified segments without correction tools
- curved MPR will feel unreliable if spatial frames and synchronization are not deterministic
- planning credibility depends on repeatable measurements more than on visual polish
- performance regressions will be noticeable immediately on large CTA datasets even on high-end hardware

## Recommendation

Start with **EVAR-focused aorto-iliac MVP** rather than trying to deliver all vascular workflows at once.

Best first release sequence:

1. editable 3D mask
2. aorto-iliac centerline
3. curved MPR + orthogonal cross-sections
4. neck / landing-zone metrics
5. planning session persistence

That path uses the strongest parts of the current codebase and gets you closest to an actual vascular workstation quickly.
