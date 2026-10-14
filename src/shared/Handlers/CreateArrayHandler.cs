using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class CreateArrayHandler : IRevitCommand
    {
        public string Name => "create_array";
        public string Description => "Copy elements into a linear or radial array. Restores with Revit Undo. One array per call.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},""kind"":{""type"":""string"",""enum"":[""linear"",""radial""]},""count"":{""type"":""integer"",""minimum"":1,""maximum"":100,""description"":""Copies to create (sources excluded).""},""step_mm"":{""type"":""object"",""properties"":{""x_mm"":{""type"":""number""},""y_mm"":{""type"":""number""},""z_mm"":{""type"":""number""}},""description"":""Per-copy offset in mm (linear only).""},""center_mm"":{""type"":""object"",""properties"":{""x_mm"":{""type"":""number""},""y_mm"":{""type"":""number""},""z_mm"":{""type"":""number""}},""description"":""Rotation center in mm (radial only).""},""angle_deg"":{""type"":""number"",""description"":""Per-copy rotation, counterclockwise around vertical through center (radial only).""},""group_result"":{""type"":""boolean"",""default"":true,""description"":""Wrap copies in a model group.""},""max_count"":{""type"":""integer"",""default"":500,""description"":""Refuse when new elements would exceed this.""}},""required"":[""element_ids"",""kind"",""count""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var kind = (req.Value<string>("kind") ?? "").ToLowerInvariant();
            if (kind != "linear" && kind != "radial")
                return CommandResult.Fail("kind must be linear or radial.");

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
            if (ids.Count == 0) return CommandResult.Fail("None of the given element_ids exist.");

            var count = req.Value<int?>("count") ?? 0;
            if (count < 1 || count > 100)
                return CommandResult.Fail("count must be between 1 and 100.");
            var maxCount = req.Value<int?>("max_count") ?? 500;
            if (maxCount <= 0) return CommandResult.Fail("max_count must be positive.");
            if ((long)ids.Count * count > maxCount)
                return CommandResult.Fail($"Array would create {ids.Count * count} elements (max_count={maxCount}). Narrow the set or raise max_count explicitly.");

            XYZ step = null;
            XYZ center = null;
            double angleRad = 0;
            if (kind == "linear")
            {
                var s = req["step_mm"] as JObject;
                if (s == null) return CommandResult.Fail("step_mm is required for linear arrays.");
                step = new XYZ(
                    s.Value<double?>("x_mm") ?? 0,
                    s.Value<double?>("y_mm") ?? 0,
                    s.Value<double?>("z_mm") ?? 0) / 304.8;
                if (step.IsZeroLength())
                    return CommandResult.Fail("step_mm must be non-zero.");
            }
            else
            {
                var c = req["center_mm"] as JObject;
                if (c == null) return CommandResult.Fail("center_mm is required for radial arrays.");
                center = new XYZ(
                    c.Value<double?>("x_mm") ?? 0,
                    c.Value<double?>("y_mm") ?? 0,
                    c.Value<double?>("z_mm") ?? 0) / 304.8;
                angleRad = (req.Value<double?>("angle_deg") ?? 0) * Math.PI / 180.0;
                if (Math.Abs(angleRad) < 1e-9)
                    return CommandResult.Fail("angle_deg must be non-zero.");
            }
            var groupResult = req.Value<bool?>("group_result") ?? true;

            using (var tx = new Transaction(doc, "RvtVerify: Create array (" + kind + " x" + count + ")"))
            {
                tx.Start();
                try
                {
                    var created = new List<long>();
                    if (kind == "linear")
                    {
                        for (var i = 1; i <= count; i++)
                        {
                            var copies = ElementTransformUtils.CopyElements(doc, ids, step * i);
                            foreach (var c in copies) created.Add(RevitCompat.GetId(c));
                        }
                    }
                    else
                    {
                        var axis = Line.CreateBound(center, center + XYZ.BasisZ);
                        var kick = new XYZ(1, 0, 0); // CopyElements needs a non-zero translation; moved back right away.
                        for (var i = 1; i <= count; i++)
                        {
                            var copies = ElementTransformUtils.CopyElements(doc, ids, kick).ToList();
                            ElementTransformUtils.MoveElements(doc, copies, -kick);
                            ElementTransformUtils.RotateElements(doc, copies, axis, angleRad * i);
                            foreach (var c in copies) created.Add(RevitCompat.GetId(c));
                        }
                    }

                    long? groupId = null;
                    string groupWarning = null;
                    if (groupResult && created.Count > 0)
                    {
                        try
                        {
                            var group = doc.Create.NewGroup(created.Select(RevitCompat.ToElementId).ToList());
                            groupId = RevitCompat.GetId(group.Id);
                        }
                        catch (Exception ex) { groupWarning = "Grouping failed, copies kept ungrouped: " + ex.Message; }
                    }

                    tx.Commit();
                    return CommandResult.Ok(new
                    {
                        kind,
                        copies = count,
                        sources = ids.Count,
                        created = created.Count,
                        createdIds = created,
                        group_id = groupId,
                        group_warning = groupWarning,
                        missing = missing.Count,
                        missingIds = missing
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail($"Array failed, nothing was changed: {ex.Message}");
                }
            }
        }
    }
}
