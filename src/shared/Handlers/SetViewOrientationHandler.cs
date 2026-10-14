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
        public string Description => "Point a 3D view camera: eye placed along direction from target, deterministic framing for screenshot comparison. Restores with Revit Undo.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""view_id"":{""type"":""integer""},""direction"":{""type"":""object"",""properties"":{""x"":{""type"":""number""},""y"":{""type"":""number""},""z"":{""type"":""number""}},""default"":{""x"":1,""y"":-1,""z"":0.7}},""target_mm"":{""type"":""object"",""properties"":{""x_mm"":{""type"":""number""},""y_mm"":{""type"":""number""},""z_mm"":{""type"":""number""}},""description"":""Look-at point in mm. Defaults to model bbox center (datum categories excluded).""},""distance_factor"":{""type"":""number"",""default"":1.5},""exclude_datums"":{""type"":""boolean"",""default"":true}}}";

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
            var distanceFactor = req.Value<double?>("distance_factor") ?? 1.5;
            var excludeDatums = req.Value<bool?>("exclude_datums") ?? true;

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
            if (distanceFactor <= 0)
                return CommandResult.Fail("direction must be non-zero and distance_factor positive.");

            XYZ target;
            double size;
            if (targetObj != null)
            {
                target = new XYZ(
                    (targetObj.Value<double?>("x_mm") ?? 0) / 304.8,
                    (targetObj.Value<double?>("y_mm") ?? 0) / 304.8,
                    (targetObj.Value<double?>("z_mm") ?? 0) / 304.8);
                var fitBox = BuildModelBox(doc, view, excludeDatums);
                size = fitBox == null ? 0 : (fitBox.Max - fitBox.Min).GetLength();
            }
            else
            {
                var box = BuildModelBox(doc, view, excludeDatums);
                if (box == null) return CommandResult.Fail("Model bounding box is empty.");
                target = (box.Min + box.Max) * 0.5;
                size = (box.Max - box.Min).GetLength();
            }

            if (size <= 0) size = 10;
            double[] fwdArr, upArr, eyeArr;
            try
            {
                var targetMm = new[] { target.X * 304.8, target.Y * 304.8, target.Z * 304.8 };
                fwdArr = FrameCalculator.ComputeForward(dirArr);
                upArr = FrameCalculator.ComputeUp(fwdArr);
                eyeArr = FrameCalculator.ComputeEye(targetMm, dirArr, size * 304.8 * distanceFactor);
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
                    return CommandResult.Ok(new
                    {
                        view_id = RevitCompat.GetId(view.Id),
                        eye_mm = new { x_mm = eye.X * 304.8, y_mm = eye.Y * 304.8, z_mm = eye.Z * 304.8 },
                        target_mm = new { x_mm = target.X * 304.8, y_mm = target.Y * 304.8, z_mm = target.Z * 304.8 },
                        size_mm = size * 304.8
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Failed to set view orientation: {ex.Message}");
                }
            }
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
