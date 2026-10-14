# revit-verify

Self-verifying Revit agent loop. Goal: the agent builds a model from a
reference (photo, plan, sketch), captures the same framing, compares,
finds deviations and fixes them — without human help until it matches
or runs out of attempts.

Built slowly, together. Human stays in the loop only for edge cases.

## Pipeline (target)

1. Reference → model description (JSON: walls, dims, openings, rooms,
   one known real size for scale).
2. Builder agent constructs the model in Revit.
3. Numeric check: dims, wall/room closure, grids, doors/windows,
   intersections, areas. Output: deviation list.
4. Observer agent (vision) compares view capture vs reference:
   layout and proportions (never pixel-perfect: light/materials differ).
5. Fixes go back to builder; loop until ok or max attempts.

## Rules

- Measurable checks first, image second. "Looks fine" without numbers
  is forbidden; disputes go to a human, never guessing.
- Every model edit undoable (TransactionGroup, one undo step).
- Small commits, everything rollbackable.

## Lineage

Ideas and (later) ported parts come from
[bimwright/rvt-mcp](https://github.com/bimwright/rvt-mcp) (Apache-2.0).
Upstream `LICENSE`/`NOTICE` obligations preserved in ported files.
This is not legal advice.
