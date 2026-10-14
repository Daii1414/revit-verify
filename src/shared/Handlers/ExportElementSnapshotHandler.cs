using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ExportElementSnapshotHandler : IRevitCommand
    {
        public string Name => "export_element_snapshot";
        public string Description => "Isolate elements, optionally tighten the 3D section box, export a raster image, then restore the view. Returns saved path. View is always restored.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},""view_id"":{""type"":""integer""},""padding_mm"":{""type"":""number"",""default"":500},""tighten_section"":{""type"":""boolean"",""default"":true},""pixel_size"":{""type"":""integer"",""default"":1600},""image_format"":{""type"":""string"",""enum"":[""png"",""jpeg""],""default"":""png""}},""required"":[""element_ids""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var idsToken = req["element_ids"] as JArray;
            if (idsToken == null || !idsToken.Any())
                return CommandResult.Fail("element_ids non-empty array is required.");
            var viewIdParam = req.Value<long?>("view_id");
            var paddingMm = req.Value<double?>("padding_mm") ?? 500;
            var tighten = req.Value<bool?>("tighten_section") ?? true;
            var pixelSize = req.Value<int?>("pixel_size") ?? 1600;
            var imageFormat = (req.Value<string>("image_format") ?? "png").ToLowerInvariant();

            var view = viewIdParam.HasValue
                ? doc.GetElement(RevitCompat.ToElementId(viewIdParam.Value)) as View
                : uidoc.ActiveView;
            if (view == null) return CommandResult.Fail("Could not resolve view.");
            if (view.IsTemplate) return CommandResult.Fail("Cannot modify a view template.");

            var ids = idsToken.Select(t => RevitCompat.ToElementId(t.Value<long>())).ToList();
            var view3d = view as View3D;
            var savedBox = view3d != null && view3d.IsSectionBoxActive ? view3d.GetSectionBox() : null;
            var boxTouched = false;

            try
            {
                using (var tx = new Transaction(doc, "RvtVerify: Isolate for snapshot"))
                {
                    tx.Start();
                    try
                    {
                        view.IsolateElementsTemporary(ids);
                        if (tighten && view3d != null && !view3d.IsLocked)
                        {
                            var tight = BuildTightBox(doc, view, ids, paddingMm / 304.8);
                            if (tight != null)
                            {
                                view3d.SetSectionBox(tight);
                                boxTouched = true;
                            }
                        }
                        tx.Commit();
                    }
                    catch (Exception ex)
                    {
                        if (tx.HasStarted()) tx.RollBack();
                        return CommandResult.Fail($"Isolate failed: {ex.Message}");
                    }
                }

                var capture = new CaptureViewImageHandler().Execute(app, new JObject
                {
                    ["view_id"] = RevitCompat.GetId(view.Id),
                    ["pixel_size"] = pixelSize,
                    ["image_format"] = imageFormat
                }.ToString());
                if (!capture.Success)
                    return CommandResult.Fail($"Isolated, but export failed: {capture.Error}. View restored.");

                return CommandResult.Ok(new
                {
                    view_id = RevitCompat.GetId(view.Id),
                    isolated_count = ids.Count,
                    section_tightened = boxTouched,
                    result = capture.Data
                });
            }
            finally
            {
                RestoreView(doc, view, view3d, savedBox, boxTouched);
            }
        }

        private static BoundingBoxXYZ BuildTightBox(Document doc, View view, List<ElementId> ids, double paddingFt)
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
            return new BoundingBoxXYZ
            {
                Min = new XYZ(min.X - paddingFt, min.Y - paddingFt, min.Z - paddingFt),
                Max = new XYZ(max.X + paddingFt, max.Y + paddingFt, max.Z + paddingFt)
            };
        }

        private static void RestoreView(Document doc, View view, View3D view3d, BoundingBoxXYZ savedBox, bool boxTouched)
        {
            try
            {
                using (var tx = new Transaction(doc, "RvtVerify: Restore after snapshot"))
                {
                    tx.Start();
                    try
                    {
                        if (boxTouched && view3d != null)
                        {
                            if (savedBox != null) view3d.SetSectionBox(savedBox);
                            else view3d.IsSectionBoxActive = false;
                        }
                        view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                        tx.Commit();
                    }
                    catch
                    {
                        if (tx.HasStarted()) tx.RollBack();
                    }
                }
            }
            catch { }
        }
    }
}
