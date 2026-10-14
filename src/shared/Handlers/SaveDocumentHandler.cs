using System;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class SaveDocumentHandler : IRevitCommand
    {
        public string Name => "save_document";
        public string Description => "Save the active document (Save) or save a copy under a new path (SaveAs). Restores nothing: saving is not undoable.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""path"":{""type"":""string"",""description"":""SaveAs target path. Empty = Save the current document in place.""},""compact"":{""type"":""boolean"",""default"":false,""description"":""Compact the file on SaveAs.""},""overwrite"":{""type"":""boolean"",""default"":false,""description"":""Allow overwriting an existing SaveAs target.""}},""required"":[]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var path = (req.Value<string>("path") ?? "").Trim();
            var compact = req.Value<bool?>("compact") ?? false;
            var overwrite = req.Value<bool?>("overwrite") ?? false;

            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    if (string.IsNullOrEmpty(doc.PathName))
                        return CommandResult.Fail("Document was never saved: provide path for SaveAs.");
                    doc.Save();
                    return CommandResult.Ok(new
                    {
                        saved = true,
                        mode = "save",
                        path = doc.PathName,
                        size_bytes = FileSize(doc.PathName),
                        workshared = doc.IsWorkshared
                    });
                }

                if (doc.IsWorkshared)
                    return CommandResult.Fail("SaveAs on a workshared model is not supported: save the local file in place instead.");
                if (File.Exists(path) && !overwrite && !string.Equals(
                        Path.GetFullPath(path), Path.GetFullPath(doc.PathName ?? ""),
                        StringComparison.OrdinalIgnoreCase))
                    return CommandResult.Fail("Target exists: pass overwrite=true to replace it.");

                var options = new SaveAsOptions
                {
                    Compact = compact,
                    OverwriteExistingFile = overwrite
                };
                doc.SaveAs(path, options);
                return CommandResult.Ok(new
                {
                    saved = true,
                    mode = "saveas",
                    path = doc.PathName,
                    size_bytes = FileSize(doc.PathName),
                    compact,
                    workshared = doc.IsWorkshared
                });
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Save failed: " + ex.Message);
            }
        }

        private static long? FileSize(string path)
        {
            try { return string.IsNullOrEmpty(path) ? (long?)null : new FileInfo(path).Length; }
            catch { return null; }
        }
    }
}
