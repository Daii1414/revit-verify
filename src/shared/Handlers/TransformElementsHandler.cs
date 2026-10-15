using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class TransformElementsHandler : IRevitCommand
    {
        public string Name => "transform_elements";
        public string Description => "Move, copy, rotate or mirror elements. Restores with Revit Undo. One operation per call.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""op"":{""type"":""string"",""enum"":[""move"",""copy"",""rotate"",""mirror""]},""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},""vector_mm"":{""type"":""object"",""properties"":{""x_mm"":{""type"":""number""},""y_mm"":{""type"":""number""},""z_mm"":{""type"":""number""}},""description"":""Translation for move/copy in mm.""},""center_mm"":{""type"":""object"",""properties"":{""x_mm"":{""type"":""number""},""y_mm"":{""type"":""number""},""z_mm"":{""type"":""number""}},""description"":""Rotation center in mm (rotate only).""},""angle_deg"":{""type"":""number"",""description"":""Counterclockwise degrees around vertical axis through center (rotate only).""},""axis_mm"":{""type"":""object"",""properties"":{""a"":{""type"":""object""},""b"":{""type"":""object""}},""description"":""Mirror axis endpoints in mm with x_mm/y_mm/z_mm; projected to horizontal, must differ in plan (mirror only).""},""mirror_copy"":{""type"":""boolean"",""default"":false,""description"":""Mirror a copy and keep the originals (mirror only).""}},""required"":[""op"",""element_ids""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var op = (req.Value<string>("op") ?? "").ToLowerInvariant();
            if (op != "move" && op != "copy" && op != "rotate" && op != "mirror")
                return CommandResult.Fail("op must be move, copy, rotate or mirror.");

            var idsToken = req["element_ids"] as JArray;
            if (idsToken == null || !idsToken.Any())
                return CommandResult.Fail("element_ids non-empty array is required.");

            var ids = new List<ElementId>();
            var missing = new List<long>();
            foreach (var t in idsToken)
            {
                var e = doc.GetElement(RevitCompat.ToElementId(t.Value<long>()));
                if (e == null) missing.Add(t.Value<long>());
                else ids.Add(e.Id);
            }
            if (ids.Count == 0)
                return CommandResult.Fail("None of the given element_ids exist.");

            XYZ vector = null;
            XYZ center = null;
            double angleRad = 0;
            Plane mirrorPlane = null;
            var mirrorCopy = false;
            if (op == "move" || op == "copy")
            {
                var v = req["vector_mm"] as JObject;
                if (v == null) return CommandResult.Fail("vector_mm is required for move/copy.");
                vector = new XYZ(
                    v.Value<double?>("x_mm") ?? 0,
                    v.Value<double?>("y_mm") ?? 0,
                    v.Value<double?>("z_mm") ?? 0) / 304.8;
                if (vector.IsZeroLength())
                    return CommandResult.Fail("vector_mm must be non-zero.");
            }
            else if (op == "mirror")
            {
                var axis = req["axis_mm"] as JObject;
                if (axis == null) return CommandResult.Fail("axis_mm with endpoints a/b is required for mirror.");
                var a = ReadPointMm(axis["a"] as JObject);
                var b = ReadPointMm(axis["b"] as JObject);
                if (a == null || b == null)
                    return CommandResult.Fail("axis_mm needs a/b objects with x_mm/y_mm/z_mm.");
                var flat = new XYZ(b.X - a.X, b.Y - a.Y, 0);
                if (flat.GetLength() < 1e-9)
                    return CommandResult.Fail("axis_mm endpoints must differ in plan.");
                var normal = new XYZ(flat.Y, -flat.X, 0).Normalize();
                mirrorPlane = Plane.CreateByNormalAndOrigin(normal, a);
                mirrorCopy = req.Value<bool?>("mirror_copy") ?? false;
            }
            else
            {
                var c = req["center_mm"] as JObject;
                if (c == null) return CommandResult.Fail("center_mm is required for rotate.");
                center = new XYZ(
                    c.Value<double?>("x_mm") ?? 0,
                    c.Value<double?>("y_mm") ?? 0,
                    c.Value<double?>("z_mm") ?? 0) / 304.8;
                angleRad = (req.Value<double?>("angle_deg") ?? 0) * Math.PI / 180.0;
                if (Math.Abs(angleRad) < 1e-9)
                    return CommandResult.Fail("angle_deg must be non-zero.");
            }

            using (var tx = new Transaction(doc, "RvtVerify: Transform elements (" + op + ")"))
            {
                tx.Start();
                try
                {
                    object detail;
                    if (op == "move")
                    {
                        ElementTransformUtils.MoveElements(doc, ids, vector);
                        detail = new { moved = ids.Count };
                    }
                    else if (op == "copy")
                    {
                        var copied = ElementTransformUtils.CopyElements(doc, ids, vector).ToList();
                        detail = new { copied = copied.Count };
                    }
                    else if (op == "rotate")
                    {
                        var axis = Line.CreateBound(center, center + XYZ.BasisZ);
                        ElementTransformUtils.RotateElements(doc, ids, axis, angleRad);
                        detail = new { rotated = ids.Count, angle_deg = req.Value<double?>("angle_deg") };
                    }
                    else
                    {
                        var mirrored = ElementTransformUtils.MirrorElements(doc, ids, mirrorPlane, mirrorCopy).ToList();
                        detail = mirrorCopy
                            ? new { mirrored_copies = mirrored.Count, mirror_copy = true, mirrored_ids = mirrored.Select(RevitCompat.GetId).ToList() }
                            : (object)new { mirrored = mirrored.Count, mirror_copy = false };
                    }
                    tx.Commit();
                    return CommandResult.Ok(new
                    {
                        op,
                        detail,
                        missing = missing.Count,
                        missingIds = missing
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Transform failed, nothing was changed: {ex.Message}");
                }
            }
        }
    
        private static XYZ ReadPointMm(JObject point)
        {
            if (point == null) return null;
            try
            {
                return new XYZ(
                    point.Value<double?>("x_mm") ?? 0,
                    point.Value<double?>("y_mm") ?? 0,
                    point.Value<double?>("z_mm") ?? 0) / 304.8;
            }
            catch { return null; }
        }
}
}
