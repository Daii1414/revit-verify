# Read-only CAD planning

`revit_cad_extract` reads geometry for CAD imports and links already loaded in
the active Revit document. It does not open arbitrary DWG files or modify the
model. Set `include_vertices=true` when planning from CAD polylines; if their
vertex lists are incomplete, the planner rejects the extraction.

`revit_cad_plan` accepts versioned JSON rules. Axis rules remain optional when
wall rules are supplied. Exact layer names are matched case-insensitively.

```json
{
  "version": "project-walls-v1",
  "wall_layers": ["A-WALL", "A-WALL-INT"],
  "wall_thickness_mm": 200,
  "wall_thickness_tolerance_mm": 10,
  "wall_angle_tolerance_degrees": 1,
  "wall_elevation_tolerance_mm": 5,
  "minimum_wall_length_mm": 500
}
```

Wall candidates are inferred from pairs of straight CAD lines or straight
polyline segments in the same CAD instance. The pair must be parallel within
the configured angle tolerance, have overlapping length, be separated by the
configured nominal thickness, and lie at compatible elevations. Candidate
centerlines include source layer, entity, segment, CAD instance and path
provenance. The plan hash covers the rules, original extraction and candidates.

`wall_analysis.analysis_complete` means only that every entity on the selected
layers was supported and every straight segment was paired without ambiguity.
Unsupported entities, unpaired segments and ambiguous pairs are returned
explicitly. Even a complete analysis is an unverified interpretation: openings,
joins, heights, materials and curved walls are not inferred. `revit_cad_audit`
compares planned axes with Revit grids and straight wall candidates with Revit
wall location-line endpoints and `Wall.Width`, using the audit tolerance for
both measurements. The comparison is read-only; it does not account for Revit
wall location-line offsets, curved walls or openings. Unmatched or unsupported
Revit walls are reported as findings when wall candidates are audited. No CAD
plan is automatically applied.
