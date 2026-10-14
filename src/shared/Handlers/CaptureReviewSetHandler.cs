using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>Captures several named frames while restoring each view after the capture.</summary>
    public sealed class CaptureReviewSetHandler : IRevitCommand
    {
        private const int MaxFrames = 8;
        private const int MaxElementsPerFrame = 200;
        private const int MaxTotalElements = 500;
        private const int MaxPixelSize = 4096;

        public string Name => "capture_review_set";
        public string Description => "Capture a bounded set of named review frames. Each frame temporarily isolates its elements, exports an image, then attempts to restore the view; exported images remain on disk. Restore any remaining view change with Revit Undo.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""frames""],
  ""properties"": {
    ""frames"": {
      ""type"": ""array"",
      ""minItems"": 1,
      ""maxItems"": 8,
      ""items"": {
        ""type"": ""object"",
        ""required"": [""name"", ""element_ids""],
        ""properties"": {
          ""name"": { ""type"": ""string"" },
          ""view_id"": { ""type"": ""integer"" },
          ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } },
          ""padding_mm"": { ""type"": ""number"", ""default"": 500 },
          ""tighten_section"": { ""type"": ""boolean"", ""default"": true },
          ""pixel_size"": { ""type"": ""integer"", ""default"": 1600 },
          ""image_format"": { ""type"": ""string"", ""enum"": [""png"", ""jpeg""] }
        }
      }
    }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            var input = request["frames"] as JArray;
            if (input == null || input.Count == 0) return CommandResult.Fail("frames must be a non-empty array.");
            if (input.Count > MaxFrames) return CommandResult.Fail("A review set accepts at most " + MaxFrames + " frames.");

            var frames = new List<JObject>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var totalElementCount = 0;
            foreach (var token in input)
            {
                if (!(token is JObject frame)) return CommandResult.Fail("Each frame must be an object.");
                var name = (frame.Value<string>("name") ?? string.Empty).Trim();
                if (name.Length == 0 || name.Length > 64) return CommandResult.Fail("Each frame name must contain 1 to 64 characters.");
                if (!names.Add(name)) return CommandResult.Fail("Frame names must be unique (case-insensitive): " + name);

                var ids = frame["element_ids"] as JArray;
                if (ids == null || ids.Count == 0) return CommandResult.Fail("Frame '" + name + "' needs a non-empty element_ids array.");
                if (ids.Count > MaxElementsPerFrame) return CommandResult.Fail("Frame '" + name + "' exceeds the " + MaxElementsPerFrame + " element limit.");
                totalElementCount += ids.Count;
                if (totalElementCount > MaxTotalElements) return CommandResult.Fail("A review set accepts at most " + MaxTotalElements + " element IDs across all frames.");

                foreach (var idToken in ids)
                {
                    if (idToken.Type != JTokenType.Integer) return CommandResult.Fail("Frame '" + name + "' contains a non-integer element ID.");
                    var id = idToken.Value<long>();
                    if (!RevitCompat.CanRepresentElementId(id)) return CommandResult.Fail("Frame '" + name + "': " + RevitCompat.ElementIdRangeError(id));
                    if (doc.GetElement(RevitCompat.ToElementId(id)) == null) return CommandResult.Fail("Frame '" + name + "' references missing element " + id + ". No frames were captured.");
                }

                var viewId = frame.Value<long?>("view_id");
                if (viewId.HasValue)
                {
                    if (!RevitCompat.CanRepresentElementId(viewId.Value)) return CommandResult.Fail("Frame '" + name + "': " + RevitCompat.ElementIdRangeError(viewId.Value));
                    var view = doc.GetElement(RevitCompat.ToElementId(viewId.Value)) as View;
                    if (view == null || view.IsTemplate) return CommandResult.Fail("Frame '" + name + "' has an invalid or template view_id. No frames were captured.");
                }
                else if (uidoc.ActiveView == null || uidoc.ActiveView.IsTemplate)
                    return CommandResult.Fail("Frame '" + name + "' needs a valid view_id because the active view is unavailable.");

                var padding = frame.Value<double?>("padding_mm") ?? 500;
                if (double.IsNaN(padding) || double.IsInfinity(padding) || padding < 0)
                    return CommandResult.Fail("Frame '" + name + "': padding_mm must be a finite non-negative number.");
                var pixelSize = frame.Value<int?>("pixel_size") ?? 1600;
                if (pixelSize < 64 || pixelSize > MaxPixelSize)
                    return CommandResult.Fail("Frame '" + name + "': pixel_size must be between 64 and " + MaxPixelSize + ".");
                var format = (frame.Value<string>("image_format") ?? "png").ToLowerInvariant();
                if (format != "png" && format != "jpeg")
                    return CommandResult.Fail("Frame '" + name + "': image_format must be png or jpeg.");

                frames.Add(new JObject
                {
                    ["name"] = name,
                    ["view_id"] = viewId.HasValue ? new JValue(viewId.Value) : null,
                    ["element_ids"] = ids.DeepClone(),
                    ["padding_mm"] = padding,
                    ["tighten_section"] = frame.Value<bool?>("tighten_section") ?? true,
                    ["pixel_size"] = pixelSize,
                    ["image_format"] = format
                });
            }

            var captured = new List<object>();
            foreach (var frame in frames)
            {
                var name = frame.Value<string>("name");
                try
                {
                    var result = new ExportElementSnapshotHandler().Execute(app, frame.ToString(Formatting.None));
                    captured.Add(new
                    {
                        name,
                        success = result.Success,
                        data = result.Success ? result.Data : null,
                        error = result.Success ? null : result.Error
                    });
                }
                catch (Exception ex)
                {
                    captured.Add(new { name, success = false, data = (object)null, error = ex.Message });
                }
            }

            var succeeded = captured.Count(frame => (bool)JObject.FromObject(frame)["success"]);
            return CommandResult.Ok(new
            {
                success = succeeded == frames.Count,
                requested_frames = frames.Count,
                captured_frames = succeeded,
                frames = captured,
                view_restore_attempted_per_frame = true
            });
        }
    }
}
