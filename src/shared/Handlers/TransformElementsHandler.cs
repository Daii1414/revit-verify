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
        public string Description => "Move, copy or rotate elements. Restores with Revit Undo. One operation per call.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""op"":{""type"":""string"",""enum"":[""move"",""copy"",""rotate""]},""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},""vector_mm"":{""type"":""object"",""properties"":{""x_mm"":{""type"":""number""},""y_mm"":{""type"":""number""},""z_mm"":{""type"":""number""}},""description"":""Translation for move/copy in mm.""},""center_mm"":{""type"":""object"",""properties"":{""x_mm"":{""type"":""number""},""y_mm"":{""type"":""number""},""z_mm"":{""type"":""number""}},""description"":""Rotation center in mm (rotate only).""},""angle_deg"":{""type"":""number"",""description"":""Counterclockwise degrees around vertical axis through center (rotate only).""}},""required"":[""op"",""element_ids""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var op = (req.Value<string>("op") ?? "").ToLowerInvariant();
            if (op != "move" && op != "copy" && op != "rotate")
                return CommandResult.Fail("op must be move, copy or rotate.");

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
                    else
                    {
                        var axis = Line.CreateBound(center, center + XYZ.BasisZ);
                        ElementTransformUtils.RotateElements(doc, ids, axis, angleRad);
                        detail = new { rotated = ids.Count, angle_deg = req.Value<double?>("angle_deg") };
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
    }
}
