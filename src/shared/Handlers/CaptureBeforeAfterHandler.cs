using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>Joint before/after frame: captures a named frame into a slot and, on the
    /// after slot, diffs it against the registered before image (same camera, same size).
    /// Pixel diffs are approximate: lighting, selection highlights and JPEG artifacts count
    /// as changes. Restore any remaining view change with Revit Undo.</summary>
    public class CaptureBeforeAfterHandler : IRevitCommand
    {
        private const int MaxElements = 200;
        private const int MaxPixelSize = 2048;
        private const int MaxRegistryEntries = 32;
        private const double CameraToleranceMm = 1.0;
        private const int PixelThreshold = 8;

        public string Name => "capture_before_after";
        public string Description => "Capture one half of a named before/after pair: isolate elements, export the frame, then restore the view. The after slot reuses the registered before image and camera and reports a pixel diff (identical/different/unverified). Restore any remaining view change with Revit Undo.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""name"":{""type"":""string"",""description"":""Pair name, 1-64 characters. The before slot registers it; the after slot looks it up.""},""slot"":{""type"":""string"",""enum"":[""before"",""after""]},""view_id"":{""type"":""integer""},""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},""padding_mm"":{""type"":""number"",""default"":500},""tighten_section"":{""type"":""boolean"",""default"":true},""pixel_size"":{""type"":""number"",""default"":1200},""image_format"":{""type"":""string"",""enum"":[""png"",""jpeg""],""default"":""png""},""max_age_hours"":{""type"":""number"",""default"":0,""description"":""Freshness bound for the baseline in hours: 0 disables the check, otherwise an older baseline yields an unverified diff.""}},""required"":[""name"",""slot"",""element_ids""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            JObject req;
            try { req = JObject.Parse(paramsJson ?? "{}"); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            var name = (req.Value<string>("name") ?? "").Trim();
            if (name.Length == 0 || name.Length > 64)
                return CommandResult.Fail("name must contain 1 to 64 characters.");
            var slot = (req.Value<string>("slot") ?? "").ToLowerInvariant();
            if (slot != "before" && slot != "after")
                return CommandResult.Fail("slot must be 'before' or 'after'.");

            var frame = ValidateFrame(doc, uidoc, req);
            if (frame.Error != null) return CommandResult.Fail(frame.Error);

            var snapshot = new ExportElementSnapshotHandler().Execute(app, frame.Spec.ToString(Formatting.None));
            if (!snapshot.Success)
                return CommandResult.Fail("Capture failed: " + snapshot.Error);
            var data = snapshot.Data as JObject ?? JObject.FromObject(snapshot.Data);
            var imagePath = data.SelectToken("result.saved_path")?.Value<string>()
                ?? data.Value<string>("saved_path");
            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
                return CommandResult.Fail("Capture produced no image file.");

            var camera = ReadCameraMm(doc, frame.ViewId);

            if (slot == "before")
            {
                var entry = new JObject
                {
                    ["name"] = name,
                    ["image_path"] = imagePath,
                    ["pixel_size"] = frame.PixelSize,
                    ["image_format"] = frame.Format,
                    ["view_id"] = frame.ViewId,
                    ["element_ids"] = new JArray(frame.ElementIds.ToArray()),
                    ["camera_mm"] = camera,
                    ["captured_utc"] = DateTime.UtcNow.ToString("o"),
                };
                try { UpsertRegistry(entry); }
                catch (Exception ex) { return CommandResult.Fail("Captured, but the before frame could not be registered: " + ex.Message); }
                return CommandResult.Ok(new
                {
                    slot,
                    name,
                    image_path = imagePath,
                    pixel_size = frame.PixelSize,
                    image_format = frame.Format,
                    view_id = frame.ViewId,
                    camera_mm = camera,
                    registered = true
                });
            }

            var baseline = LoadRegistry(name);
            if (baseline == null)
                return CommandResult.Fail("No before frame named '" + name + "'. Capture slot 'before' first.");
            var baselinePath = baseline.Value<string>("image_path");
            if (string.IsNullOrEmpty(baselinePath) || !File.Exists(baselinePath))
                return CommandResult.Fail("The before image for '" + name + "' is missing from disk. Recapture slot 'before'.");

            var changed = BeforeAfterDiff.CompareSpec(baseline, frame.ViewId, frame.PixelSize, frame.Format, frame.ElementIds.ToArray());
            var baselineCamera = baseline["camera_mm"] as JObject;
            bool? cameraMoved = null;
            if (camera != null && baselineCamera != null)
                cameraMoved = BeforeAfterDiff.CameraDistanceMm(camera, baselineCamera) > BeforeAfterDiff.CameraToleranceMm;

            var maxAgeHours = req.Value<double?>("max_age_hours") ?? 0;
            if (double.IsNaN(maxAgeHours) || double.IsInfinity(maxAgeHours) || maxAgeHours < 0)
                return CommandResult.Fail("max_age_hours must be a finite number >= 0 (0 disables the freshness check).");
            var baselineAgeHours = BeforeAfterDiff.BaselineAgeHours(baseline.Value<string>("captured_utc"), DateTime.UtcNow);

            JObject diff;
            if (BeforeAfterDiff.IsStale(baselineAgeHours, maxAgeHours))
                diff = BeforeAfterDiff.Unverified("the before frame is "
                    + (baselineAgeHours.HasValue ? Math.Round(baselineAgeHours.Value, 2) + "h old" : "undated")
                    + ", older than max_age_hours=" + maxAgeHours + "; recapture slot 'before'.");
            else
                diff = DiffImageFiles(baselinePath, imagePath, frame.Format,
                    baseline.Value<string>("image_format"), cameraMoved);

            return CommandResult.Ok(new
            {
                slot,
                name,
                image_path = imagePath,
                pixel_size = frame.PixelSize,
                image_format = frame.Format,
                view_id = frame.ViewId,
                camera_mm = camera,
                baseline = new
                {
                    image_path = baselinePath,
                    captured_utc = baseline.Value<string>("captured_utc"),
                    age_hours = baselineAgeHours.HasValue ? Math.Round(baselineAgeHours.Value, 2) : (double?)null,
                    pixel_size = baseline.Value<int?>("pixel_size"),
                    image_format = baseline.Value<string>("image_format"),
                },
                spec_changed = changed,
                camera_moved = cameraMoved,
                diff,
            });
        }

        private sealed class Frame
        {
            public string Error;
            public JObject Spec;
            public long ViewId;
            public List<long> ElementIds;
            public int PixelSize;
            public string Format;
        }

        private static Frame ValidateFrame(Document doc, UIDocument uidoc, JObject req)
        {
            var frame = new Frame();
            var idsToken = req["element_ids"] as JArray;
            if (idsToken == null || !idsToken.Any())
            {
                frame.Error = "element_ids non-empty array is required.";
                return frame;
            }
            if (idsToken.Count > MaxElements)
            {
                frame.Error = "element_ids accepts at most " + MaxElements + " IDs.";
                return frame;
            }
            var ids = new List<long>();
            foreach (var token in idsToken)
            {
                if (token.Type != JTokenType.Integer)
                {
                    frame.Error = "element_ids must contain only integers.";
                    return frame;
                }
                var id = token.Value<long>();
                if (!RevitCompat.CanRepresentElementId(id))
                {
                    frame.Error = RevitCompat.ElementIdRangeError(id);
                    return frame;
                }
                if (doc.GetElement(RevitCompat.ToElementId(id)) == null)
                {
                    frame.Error = "element " + id + " not found. Nothing was captured.";
                    return frame;
                }
                ids.Add(id);
            }
            var viewIdParam = req.Value<long?>("view_id");
            View view;
            if (viewIdParam.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(viewIdParam.Value))
                {
                    frame.Error = RevitCompat.ElementIdRangeError(viewIdParam.Value);
                    return frame;
                }
                view = doc.GetElement(RevitCompat.ToElementId(viewIdParam.Value)) as View;
            }
            else
            {
                view = uidoc.ActiveView;
            }
            if (view == null || view.IsTemplate)
            {
                frame.Error = "Could not resolve a usable view (provide view_id).";
                return frame;
            }
            var padding = req.Value<double?>("padding_mm") ?? 500;
            if (double.IsNaN(padding) || double.IsInfinity(padding) || padding < 0)
            {
                frame.Error = "padding_mm must be a finite non-negative number.";
                return frame;
            }
            var pixelSize = req.Value<int?>("pixel_size") ?? 1200;
            if (pixelSize < 64 || pixelSize > MaxPixelSize)
            {
                frame.Error = "pixel_size must be between 64 and " + MaxPixelSize + " (diff cost is bounded).";
                return frame;
            }
            var format = (req.Value<string>("image_format") ?? "png").ToLowerInvariant();
            if (format != "png" && format != "jpeg")
            {
                frame.Error = "image_format must be png or jpeg.";
                return frame;
            }
            frame.ViewId = RevitCompat.GetId(view.Id);
            frame.ElementIds = ids;
            frame.PixelSize = pixelSize;
            frame.Format = format;
            frame.Spec = new JObject
            {
                ["element_ids"] = new JArray(ids.ToArray()),
                ["view_id"] = frame.ViewId,
                ["padding_mm"] = padding,
                ["tighten_section"] = req.Value<bool?>("tighten_section") ?? true,
                ["pixel_size"] = pixelSize,
                ["image_format"] = format,
            };
            return frame;
        }

        private static JObject ReadCameraMm(Document doc, long viewId)
        {
            try
            {
                var view = doc.GetElement(RevitCompat.ToElementId(viewId)) as View3D;
                if (view == null) return null;
                var orientation = view.GetOrientation();
                if (orientation == null) return null;
                return new JObject
                {
                    ["eye"] = PointMm(orientation.EyePosition),
                    ["target"] = PointMm(orientation.EyePosition + orientation.ForwardDirection),
                    ["up"] = PointMm(orientation.UpDirection),
                };
            }
            catch { return null; }
        }

        private static JObject PointMm(XYZ point)
        {
            return new JObject
            {
                ["x_mm"] = point.X * 304.8,
                ["y_mm"] = point.Y * 304.8,
                ["z_mm"] = point.Z * 304.8,
            };
        }

        private static JObject DiffImageFiles(string baselinePath, string afterPath, string afterFormat,
            string baselineFormat, bool? cameraMoved)
        {
            Bitmap baseline;
            Bitmap after;
            try { baseline = new Bitmap(baselinePath); }
            catch { return BeforeAfterDiff.Unverified("baseline image cannot be read; recapture the before frame."); }
            try { after = new Bitmap(afterPath); }
            catch
            {
                baseline.Dispose();
                return BeforeAfterDiff.Unverified("after image cannot be read.");
            }
            try
            {
                return BeforeAfterDiff.DiffImages(baseline, after,
                    !string.Equals(baselineFormat, afterFormat, StringComparison.OrdinalIgnoreCase), cameraMoved);
            }
            finally
            {
                baseline.Dispose();
                after.Dispose();
            }
        }

        private static string RegistryPath()
        {
            return Path.Combine(PathAllowlist.CapturesDirectory, "before-after.json");
        }

        private static void UpsertRegistry(JObject entry)
        {
            var all = ReadRegistry();
            var name = entry.Value<string>("name");
            for (var i = 0; i < all.Count; i++)
            {
                if (string.Equals((all[i] as JObject)?.Value<string>("name"), name, StringComparison.OrdinalIgnoreCase))
                {
                    all[i] = entry;
                    WriteRegistry(all);
                    return;
                }
            }
            all.Add(entry);
            while (all.Count > MaxRegistryEntries)
            {
                JToken oldest = null;
                DateTime oldestTime = DateTime.MaxValue;
                foreach (JObject e in all.OfType<JObject>())
                {
                    DateTime t;
                    if (DateTime.TryParse(e.Value<string>("captured_utc"), out t) && t < oldestTime)
                    {
                        oldestTime = t;
                        oldest = e;
                    }
                }
                if (oldest == null) break;
                all.Remove(oldest);
            }
            WriteRegistry(all);
        }

        private static JObject LoadRegistry(string name)
        {
            var all = ReadRegistry();
            var match = all.OfType<JObject>()
                .FirstOrDefault(e => string.Equals(e.Value<string>("name"), name, StringComparison.OrdinalIgnoreCase));
            if (match == null) return null;
            var path = match.Value<string>("image_path");
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                all.Remove(match);
                try { WriteRegistry(all); } catch { }
                return null;
            }
            return match;
        }

        private static JArray ReadRegistry()
        {
            try
            {
                var path = RegistryPath();
                if (!File.Exists(path)) return new JArray();
                var parsed = JToken.Parse(File.ReadAllText(path));
                return parsed as JArray ?? new JArray();
            }
            catch { return new JArray(); }
        }

        private static void WriteRegistry(JArray all)
        {
            var path = RegistryPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, all.ToString(Formatting.None));
        }
    }
}
