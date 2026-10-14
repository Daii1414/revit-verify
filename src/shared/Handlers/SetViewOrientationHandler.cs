using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class SetViewOrientationHandler : IRevitCommand
    {
        public string Name => "set_view_orientation";
        public string Description => "Set a 3D view camera for repeatable review. Use eye_mm + target_mm for an exact camera position, optionally up for camera roll; or direction with a model-derived target and distance. Restores with Revit Undo.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""view_id"":{""type"":""integer""},""direction"":{""type"":""object"",""properties"":{""x"":{""type"":""number""},""y"":{""type"":""number""},""z"":{""type"":""number""}},""default"":{""x"":1,""y"":-1,""z"":0.7}},""target_mm"":{""type"":""object"",""properties"":{""x_mm"":{""type"":""number""},""y_mm"":{""type"":""number""},""z_mm"":{""type"":""number""}},""description"":""Look-at point in mm. Defaults to element box center, else model bbox center (datum categories excluded).""},""eye_mm"":{""type"":""object"",""properties"":{""x_mm"":{""type"":""number""},""y_mm"":{""type"":""number""},""z_mm"":{""type"":""number""}},""description"":""Exact camera position in mm. Requires target_mm; overrides direction and distance.""},""up"":{""type"":""object"",""properties"":{""x"":{""type"":""number""},""y"":{""type"":""number""},""z"":{""type"":""number""}},""description"":""Preferred camera up vector. Projected onto the plane perpendicular to the view direction.""},""element_ids"":{""type"":""array"",""items"":{""type"":""integer""},""description"":""Frame exactly these elements (ids). Beats model-wide auto framing.""},""distance_factor"":{""type"":""number"",""default"":1.5},""distance_mm"":{""type"":""number"",""description"":""Explicit eye distance in mm. Beats distance_factor when positive.""},""exclude_datums"":{""type"":""boolean"",""default"":true},""zoom_to_fit"":{""type"":""boolean"",""default"":true,""description"":""Zoom the open view to fit after orienting (deterministic framing). Skipped silently if the view is not open on screen.""}}}";

        private static readonly long[] DatumCategories =
        {
            (long)BuiltInCategory.OST_Grids,
            (long)BuiltInCategory.OST_Levels,
            (long)BuiltInCategory.OST_Sections,
            (long)BuiltInCategory.OST_Elev,
            (long)BuiltInCategory.OST_Cameras,
        };

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var viewIdParam = req.Value<long?>("view_id");
            var dirObj = req["direction"] as JObject;
            var targetObj = req["target_mm"] as JObject;
            var eyeObj = req["eye_mm"] as JObject;
            var upObj = req["up"] as JObject;
            var distanceFactor = req.Value<double?>("distance_factor") ?? 1.5;
            var distanceMmParam = req.Value<double?>("distance_mm");
            var excludeDatums = req.Value<bool?>("exclude_datums") ?? true;
            var zoomToFit = req.Value<bool?>("zoom_to_fit") ?? true;
            var elementIdsToken = req["element_ids"] as JArray;

            var view = viewIdParam.HasValue
                ? doc.GetElement(RevitCompat.ToElementId(viewIdParam.Value)) as View3D
                : uidoc.ActiveView as View3D;
            if (view == null) return CommandResult.Fail("Could not resolve a 3D view (provide view_id of a 3D view).");
            if (view.IsTemplate) return CommandResult.Fail("Cannot modify a view template.");

            var dirArr = new[]
            {
                dirObj?.Value<double?>("x") ?? 1,
                dirObj?.Value<double?>("y") ?? -1,
                dirObj?.Value<double?>("z") ?? 0.7,
            };
            if (eyeObj != null && targetObj == null)
                return CommandResult.Fail("eye_mm requires target_mm for an exact camera pose.");
            if (eyeObj == null && distanceFactor <= 0)
                return CommandResult.Fail("direction must be non-zero and distance_factor positive.");

            XYZ target;
            double size;
            int framedCount = 0;
            int missingCount = 0;
            BoundingBoxXYZ elementBox = null;
            if (elementIdsToken != null && elementIdsToken.Count > 0)
            {
                var resolved = new List<ElementId>();
                foreach (var t in elementIdsToken)
                {
                    var el = doc.GetElement(RevitCompat.ToElementId(t.Value<long>()));
                    if (el == null) missingCount++;
                    else { resolved.Add(el.Id); framedCount++; }
                }
                if (framedCount > 0)
                    elementBox = BuildIdBox(doc, view, resolved);
            }
            if (targetObj != null)
            {
                if (eyeObj != null)
                {
                    var targetCoords = ReadPoint(targetObj, "x_mm", "y_mm", "z_mm");
                    if (targetCoords == null) return CommandResult.Fail("target_mm needs finite x_mm, y_mm, and z_mm values for an exact camera pose.");
                    target = new XYZ(targetCoords[0] / 304.8, targetCoords[1] / 304.8, targetCoords[2] / 304.8);
                }
                else
                {
                    target = new XYZ(
                        (targetObj.Value<double?>("x_mm") ?? 0) / 304.8,
                        (targetObj.Value<double?>("y_mm") ?? 0) / 304.8,
                        (targetObj.Value<double?>("z_mm") ?? 0) / 304.8);
                }
                var fitBox = elementBox ?? BuildModelBox(doc, view, excludeDatums);
                size = fitBox == null ? 0 : (fitBox.Max - fitBox.Min).GetLength();
            }
            else if (elementBox != null)
            {
                target = (elementBox.Min + elementBox.Max) * 0.5;
                size = (elementBox.Max - elementBox.Min).GetLength();
            }
            else
            {
                if (elementIdsToken != null && elementIdsToken.Count > 0)
                    return CommandResult.Fail("None of the given element_ids resolved to visible geometry.");
                var box = BuildModelBox(doc, view, excludeDatums);
                if (box == null) return CommandResult.Fail("Model bounding box is empty.");
                target = (box.Min + box.Max) * 0.5;
                size = (box.Max - box.Min).GetLength();
            }

            if (size <= 0) size = 10;
            double[] fwdArr, upArr, eyeArr;
            double distanceMm;
            try
            {
                var targetMm = new[] { target.X * 304.8, target.Y * 304.8, target.Z * 304.8 };
                if (eyeObj != null)
                {
                    eyeArr = ReadPoint(eyeObj, "x_mm", "y_mm", "z_mm");
                    if (eyeArr == null) return CommandResult.Fail("eye_mm needs finite x_mm, y_mm, and z_mm values.");
                    dirArr = new[] { eyeArr[0] - targetMm[0], eyeArr[1] - targetMm[1], eyeArr[2] - targetMm[2] };
                    fwdArr = FrameCalculator.ComputeForward(dirArr);
                    distanceMm = FrameCalculator.Length(dirArr);
                }
                else
                {
                    fwdArr = FrameCalculator.ComputeForward(dirArr);
                    distanceMm = FrameCalculator.ResolveDistance(size * 304.8, distanceFactor, distanceMmParam);
                    eyeArr = FrameCalculator.ComputeEye(targetMm, dirArr, distanceMm);
                }
                var preferredUp = upObj == null ? null : ReadPoint(upObj, "x", "y", "z");
                if (upObj != null && preferredUp == null)
                    return CommandResult.Fail("up needs finite x, y, and z values.");
                upArr = FrameCalculator.ComputeUp(fwdArr, preferredUp);
            }
            catch (ArgumentException ex)
            {
                return CommandResult.Fail(ex.Message);
            }
            var forward = new XYZ(fwdArr[0], fwdArr[1], fwdArr[2]);
            var up = new XYZ(upArr[0], upArr[1], upArr[2]);
            var eye = new XYZ(eyeArr[0] / 304.8, eyeArr[1] / 304.8, eyeArr[2] / 304.8);

            using (var tx = new Transaction(doc, "RvtMcp: Set view orientation"))
            {
                tx.Start();
                try
                {
                    view.SetOrientation(new ViewOrientation3D(eye, up, forward));
                    tx.Commit();
                    var zoomed = false;
                    if (zoomToFit)
                        zoomed = TryZoomToFit(uidoc, view);
                    return CommandResult.Ok(new
                    {
                        view_id = RevitCompat.GetId(view.Id),
                        eye_mm = new { x_mm = eye.X * 304.8, y_mm = eye.Y * 304.8, z_mm = eye.Z * 304.8 },
                        target_mm = new { x_mm = target.X * 304.8, y_mm = target.Y * 304.8, z_mm = target.Z * 304.8 },
                        size_mm = size * 304.8,
                        distance_mm = distanceMm,
                        zoomed_to_fit = zoomed,
                        framed_ids = framedCount,
                        missing_ids = missingCount
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to set view orientation: {ex.Message}");
                }
            }
        }

        private static bool TryZoomToFit(UIDocument uidoc, View view)
        {
            try
            {
                foreach (var uv in uidoc.GetOpenUIViews())
                {
                    if (uv.ViewId.Equals(view.Id))
                    {
                        uv.ZoomToFit();
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static double[] ReadPoint(JObject point, string xKey, string yKey, string zKey)
        {
            if (point == null) return null;
            try
            {
                var x = point.Value<double?>(xKey);
                var y = point.Value<double?>(yKey);
                var z = point.Value<double?>(zKey);
                if (!x.HasValue || !y.HasValue || !z.HasValue
                    || double.IsNaN(x.Value) || double.IsInfinity(x.Value)
                    || double.IsNaN(y.Value) || double.IsInfinity(y.Value)
                    || double.IsNaN(z.Value) || double.IsInfinity(z.Value)) return null;
                return new[] { x.Value, y.Value, z.Value };
            }
            catch { return null; }
        }

        private static BoundingBoxXYZ BuildIdBox(Document doc, View view, List<ElementId> ids)
        {
            XYZ min = null;
            XYZ max = null;
            foreach (var id in ids)
            {
                var box = doc.GetElement(id)?.get_BoundingBox(view);
                if (box == null) continue;
                min = min == null
                    ? box.Min
                    : new XYZ(Math.Min(min.X, box.Min.X), Math.Min(min.Y, box.Min.Y), Math.Min(min.Z, box.Min.Z));
                max = max == null
                    ? box.Max
                    : new XYZ(Math.Max(max.X, box.Max.X), Math.Max(max.Y, box.Max.Y), Math.Max(max.Z, box.Max.Z));
            }
            if (min == null || max == null) return null;
            return new BoundingBoxXYZ { Min = min, Max = max };
        }

        private static BoundingBoxXYZ BuildModelBox(Document doc, View view, bool excludeDatums)
        {
            var excluded = excludeDatums ? new HashSet<long>(DatumCategories) : new HashSet<long>();
            XYZ min = null;
            XYZ max = null;
            int n = 0;

            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                if (n++ > 50000) break;
                if (e is View) continue; // views/cameras report huge boxes, not model geometry
                if (e is ImportInstance) continue; // reference underlays (DWG) must not drag framing
                if (e is RevitLinkInstance) continue; // linked models must not drag framing
                if (excludeDatums && e.Category != null && excluded.Contains(RevitCompat.GetId(e.Category.Id)))
                    continue;
                var box = e.get_BoundingBox(view);
                if (box == null) continue;
                min = min == null
                    ? box.Min
                    : new XYZ(Math.Min(min.X, box.Min.X), Math.Min(min.Y, box.Min.Y), Math.Min(min.Z, box.Min.Z));
                max = max == null
                    ? box.Max
                    : new XYZ(Math.Max(max.X, box.Max.X), Math.Max(max.Y, box.Max.Y), Math.Max(max.Z, box.Max.Z));
            }

            if (min == null || max == null) return null;
            return new BoundingBoxXYZ { Min = min, Max = max };
        }
    }
}
