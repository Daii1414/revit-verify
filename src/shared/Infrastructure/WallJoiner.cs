using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// Joins a freshly created wall to the walls it crosses, inside the same
    /// transaction. Joined geometry stops Revit from asking "walls overlap, join them?"
    /// which removes the popup entirely instead of answering it afterwards.
    /// </summary>
    public static class WallJoiner
    {
        public static void TryJoinNearby(Document doc, Wall created)
        {
            if (doc == null || created == null) return;
            var line = created.Location as LocationCurve;
            if (line == null) return;
            var a1 = line.Curve.GetEndPoint(0);
            var a2 = line.Curve.GetEndPoint(1);
            foreach (var candidate in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>())
            {
                if (candidate.Id == created.Id) continue;
                var other = candidate.Location as LocationCurve;
                if (other == null || other.Curve is not Line otherLine) continue;
                if (!SegmentsCross(a1, a2, other.Curve.GetEndPoint(0), other.Curve.GetEndPoint(1))) continue;
                try { JoinGeometryUtils.JoinGeometry(doc, created, candidate); }
                catch { /* cross-type or already joined: leave for OverlapDialogResponder */ }
            }
        }

        // 2D segment intersection (XY plane). Walls are vertical, so plan crossing is
        // exactly what triggers Revit's overlap prompt.
        private static bool SegmentsCross(XYZ p1, XYZ p2, XYZ q1, XYZ q2)
        {
            double D(double x1, double y1, double x2, double y2, double x3, double y3) =>
                (x2 - x1) * (y3 - y1) - (y2 - y1) * (x3 - x1);
            double d1 = D(p1.X, p1.Y, p2.X, p2.Y, q1.X, q1.Y);
            double d2 = D(p1.X, p1.Y, p2.X, p2.Y, q2.X, q2.Y);
            double d3 = D(q1.X, q1.Y, q2.X, q2.Y, p1.X, p1.Y);
            double d4 = D(q1.X, q1.Y, q2.X, q2.Y, p2.X, p2.Y);
            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
                return true;
            // Collinear/touching cases: treat near-zero as crossing; join is idempotent-ish.
            return false;
        }
    }
}
