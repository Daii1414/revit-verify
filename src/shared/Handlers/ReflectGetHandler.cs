using System;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class ReflectGetHandler : IRevitCommand
    {
        public string Name => "reflect_get";
        public string Description => "Read one property of one element by path without send_code: id, name, category, type, level, host, location, box, or param:<Name> (instance first, then type). Read-only. Unset values return null with a note instead of failing.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""element_id"":{""type"":""integer""},""path"":{""type"":""string"",""description"":""One of: id, name, category, type, level, host, location, box, param:<Name>. No nesting.""}},""required"":[""element_id"",""path""]}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            JObject req;
            try { req = JObject.Parse(paramsJson ?? "{}"); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            var id = req.Value<long?>("element_id");
            if (!id.HasValue) return CommandResult.Fail("element_id is required.");
            if (!RevitCompat.CanRepresentElementId(id.Value))
                return CommandResult.Fail(RevitCompat.ElementIdRangeError(id.Value));

            ReflectPathParser.ParsedPath parsed;
            try { parsed = ReflectPathParser.Parse(req.Value<string>("path")); }
            catch (ReflectPathParser.ParseException ex) { return CommandResult.Fail(ex.Message); }

            var element = doc.GetElement(RevitCompat.ToElementId(id.Value));
            if (element == null) return CommandResult.Fail("Element " + id.Value + " not found.");

            try
            {
                return CommandResult.Ok(ReadValue(doc, element, id.Value, parsed));
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("reflect_get failed: " + ex.Message);
            }
        }

        private static object ReadValue(Document doc, Element element, long id, ReflectPathParser.ParsedPath parsed)
        {
            switch (parsed.Kind)
            {
                case "id": return Result(id, parsed, RevitCompat.GetId(element.Id));
                case "name": return Result(id, parsed, Safe(() => element.Name, out var note), note);
                case "category": return Result(id, parsed, element.Category?.Name, element.Category == null ? "element has no category" : null);
                case "type": return ReadType(doc, element, id, parsed);
                case "level": return ReadLevel(doc, element, id, parsed);
                case "host": return ReadHost(doc, element, id, parsed);
                case "location": return ReadLocation(element, id, parsed);
                case "box": return ReadBox(element, id, parsed);
                default: return ReadParameter(doc, element, id, parsed);
            }
        }

        private static object Result(long id, ReflectPathParser.ParsedPath parsed, object value, string note = null)
        {
            return new
            {
                element_id = id,
                path = parsed.Kind == "param" ? "param:" + parsed.ParameterName : parsed.Kind,
                value,
                note
            };
        }

        private static string Safe(Func<string> read, out string note)
        {
            note = null;
            try
            {
                var value = read();
                if (value == null) note = "value is not set";
                return value;
            }
            catch (Exception ex) { note = "unreadable: " + ex.Message; return null; }
        }

        private static object ReadType(Document doc, Element element, long id, ReflectPathParser.ParsedPath parsed)
        {
            try
            {
                var typeId = element.GetTypeId();
                if (typeId == null || typeId == ElementId.InvalidElementId)
                    return Result(id, parsed, null, "element has no type");
                var typeName = doc.GetElement(typeId)?.Name;
                return Result(id, parsed, typeName, typeName == null ? "type element not found" : null);
            }
            catch (Exception ex) { return Result(id, parsed, null, "unreadable: " + ex.Message); }
        }

        private static object ReadLevel(Document doc, Element element, long id, ReflectPathParser.ParsedPath parsed)
        {
            try
            {
                var levelId = element.LevelId;
                if (levelId == null || levelId == ElementId.InvalidElementId)
                    return Result(id, parsed, null, "element has no level");
                var level = doc.GetElement(levelId) as Level;
                if (level == null)
                    return Result(id, parsed, null, "level reference is dangling");
                return Result(id, parsed, level.Name);
            }
            catch (Exception ex) { return Result(id, parsed, null, "unreadable: " + ex.Message); }
        }

        private static object ReadHost(Document doc, Element element, long id, ReflectPathParser.ParsedPath parsed)
        {
            try
            {
                if (!(element is FamilyInstance))
                    return Result(id, parsed, null, "element is not a hosted family instance");
                FamilyInstance instance = (FamilyInstance)element;
                Element host;
                try { host = instance.Host; }
                catch { return Result(id, parsed, null, "family is not hostable"); }
                if (host == null || doc.GetElement(host.Id) == null)
                    return Result(id, parsed, null, "host is missing");
                return Result(id, parsed, RevitCompat.GetId(host.Id));
            }
            catch (Exception ex) { return Result(id, parsed, null, "unreadable: " + ex.Message); }
        }

        private static object ReadLocation(Element element, long id, ReflectPathParser.ParsedPath parsed)
        {
            try
            {
                var location = element.Location;
                if (location is LocationPoint point)
                {
                    var p = point.Point;
                    return Result(id, parsed, new
                    {
                        kind = "point",
                        x_mm = p.X * 304.8,
                        y_mm = p.Y * 304.8,
                        z_mm = p.Z * 304.8
                    });
                }
                if (location is LocationCurve curve)
                {
                    var c = curve.Curve;
                    return Result(id, parsed, new
                    {
                        kind = "curve",
                        start_mm = P(c.GetEndPoint(0)),
                        end_mm = P(c.GetEndPoint(1)),
                        length_mm = c.ApproximateLength * 304.8
                    });
                }
                return Result(id, parsed, null, "element has no point or curve location");
            }
            catch (Exception ex) { return Result(id, parsed, null, "unreadable: " + ex.Message); }
        }

        private static object ReadBox(Element element, long id, ReflectPathParser.ParsedPath parsed)
        {
            try
            {
                var box = element.get_BoundingBox(null);
                if (box == null)
                    return Result(id, parsed, null, "element has no bounding box");
                return Result(id, parsed, new { min_mm = P(box.Min), max_mm = P(box.Max) });
            }
            catch (Exception ex) { return Result(id, parsed, null, "unreadable: " + ex.Message); }
        }

        private static object P(XYZ point)
        {
            return new { x_mm = point.X * 304.8, y_mm = point.Y * 304.8, z_mm = point.Z * 304.8 };
        }

        private static object ReadParameter(Document doc, Element element, long id, ReflectPathParser.ParsedPath parsed)
        {
            try
            {
                var parameter = FindParameter(element, parsed.ParameterName, out var fromType);
                if (parameter == null)
                    return new { element_id = id, path = "param:" + parsed.ParameterName, value = (object)null, note = "parameter not found on element (instance nor type)" };
                return Result(id, parsed, RenderParameter(parameter, fromType, out var note), note);
            }
            catch (Exception ex) { return Result(id, parsed, null, "unreadable: " + ex.Message); }
        }

        private static Parameter FindParameter(Element element, string name, out bool fromType)
        {
            fromType = false;
            foreach (Parameter candidate in element.Parameters)
            {
                if (candidate?.Definition?.Name != null
                    && candidate.Definition.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            try
            {
                var typeId = element.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                {
                    var type = element.Document.GetElement(typeId);
                    if (type != null)
                    {
                        foreach (Parameter candidate in type.Parameters)
                        {
                            if (candidate?.Definition?.Name != null
                                && candidate.Definition.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                            {
                                fromType = true;
                                return candidate;
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static object RenderParameter(Parameter parameter, bool fromType, out string note)
        {
            note = fromType ? "value comes from the element type" : null;
            try
            {
                var storage = parameter.StorageType.ToString();
                string display;
                try { display = parameter.AsValueString(); }
                catch { display = null; }
                object raw;
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        raw = parameter.AsString();
                        break;
                    case StorageType.Integer:
                        raw = parameter.AsInteger();
                        break;
                    case StorageType.Double:
                        raw = parameter.AsDouble();
                        break;
                    case StorageType.ElementId:
                        raw = (object)RevitCompat.GetIdOrNull(parameter.AsElementId());
                        break;
                    default:
                        raw = null;
                        break;
                }
                if (display == null && raw != null)
                    display = Convert.ToString(raw, CultureInfo.InvariantCulture);
                return new
                {
                    display,
                    storage,
                    read_only = SafeReadOnly(parameter),
                    from_type = fromType,
                    raw
                };
            }
            catch (Exception ex)
            {
                note = "unreadable: " + ex.Message;
                return null;
            }
        }

        private static bool SafeReadOnly(Parameter parameter)
        {
            try { return parameter.IsReadOnly; }
            catch { return true; }
        }
    }
}
