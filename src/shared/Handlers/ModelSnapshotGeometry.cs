using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>Captures bounded, order-independent evidence from Revit's tessellated geometry.</summary>
    internal static class ModelSnapshotGeometry
    {
        private const double FeetToMm = 304.8;
        private const double QuantumMm = 0.5;
        private const int MaxPrimitives = 50000;

        internal static JObject Capture(Document doc, Element element)
        {
            var typeUniqueId = ReadTypeUniqueId(doc, element);
            var bbox = ReadBoundingBox(element);
            var geometry = Fingerprint(element);
            return new JObject
            {
                ["unique_id"] = element.UniqueId,
                ["element_id"] = RevitCompat.GetId(element.Id),
                ["category_id"] = element.Category == null ? JValue.CreateNull() : new JValue(RevitCompat.GetId(element.Category.Id)),
                ["category"] = element.Category?.Name,
                ["type_unique_id"] = typeUniqueId == null ? JValue.CreateNull() : new JValue(typeUniqueId),
                ["name"] = SafeName(element),
                ["bounding_box_mm"] = bbox,
                ["geometry_status"] = geometry.Verified ? "verified" : "unverified",
                ["geometry_sha256"] = geometry.Verified ? new JValue(geometry.Hash) : JValue.CreateNull(),
                ["triangle_count"] = geometry.TriangleCount,
                ["curve_segment_count"] = geometry.CurveSegmentCount,
                ["geometry_warning"] = geometry.Warning
            };
        }

        private static FingerprintResult Fingerprint(Element element)
        {
            try
            {
                var options = new Options { ComputeReferences = false, IncludeNonVisibleObjects = false, DetailLevel = ViewDetailLevel.Fine };
                var geometry = element.get_Geometry(options);
                if (geometry == null) return FingerprintResult.Unverified("geometry_unavailable");
                var builder = new FingerprintBuilder(MaxPrimitives);
                foreach (GeometryObject item in geometry)
                    if (!Traverse(item, builder, 0)) return FingerprintResult.Unverified(builder.Warning);
                if (builder.PrimitiveHashes.Count == 0) return FingerprintResult.Unverified("no_supported_geometry");
                builder.PrimitiveHashes.Sort(StringComparer.Ordinal);
                var hash = Hash(Encoding.UTF8.GetBytes(ModelSnapshotContract.GeometryMethod + "\n" + string.Join("\n", builder.PrimitiveHashes)));
                return new FingerprintResult(true, hash, builder.TriangleCount, builder.CurveSegmentCount, null);
            }
            catch { return FingerprintResult.Unverified("geometry_read_failed"); }
        }

        private static bool Traverse(GeometryObject item, FingerprintBuilder builder, int depth)
        {
            if (item == null) return true;
            if (depth > 16) { builder.Warning = "geometry_nesting_limit"; return false; }
            if (item is GeometryInstance instance)
            {
                var nested = instance.GetInstanceGeometry();
                if (nested == null) return true;
                foreach (GeometryObject child in nested)
                    if (!Traverse(child, builder, depth + 1)) return false;
                return true;
            }
            if (item is Solid solid)
            {
                foreach (Face face in solid.Faces)
                {
                    var mesh = face.Triangulate();
                    if (!AddMesh(mesh, builder)) return false;
                }
                return true;
            }
            if (item is Mesh meshItem) return AddMesh(meshItem, builder);
            if (item is Curve curve)
            {
                var points = curve.IsBound ? curve.Tessellate() : new List<XYZ>();
                for (var i = 1; i < points.Count; i++)
                {
                    if (!builder.AddSegment(points[i - 1], points[i])) return false;
                }
                return true;
            }
            if (item is PolyLine polyline)
            {
                var points = polyline.GetCoordinates();
                for (var i = 1; i < points.Count; i++)
                    if (!builder.AddSegment(points[i - 1], points[i])) return false;
                return true;
            }
            if (item is Point point) return builder.AddPoint(point.Coord);
            return true;
        }

        private static bool AddMesh(Mesh mesh, FingerprintBuilder builder)
        {
            if (mesh == null) return true;
            if (mesh.NumTriangles > builder.Remaining)
            { builder.Warning = "geometry_primitive_limit"; return false; }
            for (var i = 0; i < mesh.NumTriangles; i++)
            {
                var triangle = mesh.get_Triangle(i);
                if (!builder.AddTriangle(triangle.get_Vertex(0), triangle.get_Vertex(1), triangle.get_Vertex(2))) return false;
            }
            return true;
        }

        private static JObject ReadBoundingBox(Element element)
        {
            var box = element.get_BoundingBox(null);
            if (box == null) return null;
            var minX = double.PositiveInfinity; var minY = double.PositiveInfinity; var minZ = double.PositiveInfinity;
            var maxX = double.NegativeInfinity; var maxY = double.NegativeInfinity; var maxZ = double.NegativeInfinity;
            for (var corner = 0; corner < 8; corner++)
            {
                var point = new XYZ((corner & 1) == 0 ? box.Min.X : box.Max.X,
                    (corner & 2) == 0 ? box.Min.Y : box.Max.Y,
                    (corner & 4) == 0 ? box.Min.Z : box.Max.Z);
                point = box.Transform.OfPoint(point);
                minX = Math.Min(minX, point.X * FeetToMm); minY = Math.Min(minY, point.Y * FeetToMm); minZ = Math.Min(minZ, point.Z * FeetToMm);
                maxX = Math.Max(maxX, point.X * FeetToMm); maxY = Math.Max(maxY, point.Y * FeetToMm); maxZ = Math.Max(maxZ, point.Z * FeetToMm);
            }
            return new JObject
            {
                ["min_mm"] = new JArray(Math.Round(minX, 3), Math.Round(minY, 3), Math.Round(minZ, 3)),
                ["max_mm"] = new JArray(Math.Round(maxX, 3), Math.Round(maxY, 3), Math.Round(maxZ, 3))
            };
        }

        private static string ReadTypeUniqueId(Document doc, Element element)
        {
            try
            {
                var typeId = element.GetTypeId();
                return typeId == null || typeId == ElementId.InvalidElementId ? null : doc.GetElement(typeId)?.UniqueId;
            }
            catch { return null; }
        }

        private static string SafeName(Element element)
        {
            try { return element.Name ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static string Quantize(double feet)
        {
            var units = Math.Round(feet * FeetToMm / QuantumMm, MidpointRounding.AwayFromZero);
            return units.ToString("0", CultureInfo.InvariantCulture);
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
        }

        private sealed class FingerprintBuilder
        {
            private readonly int _maxPrimitives;
            internal FingerprintBuilder(int maxPrimitives) { _maxPrimitives = maxPrimitives; }
            internal List<string> PrimitiveHashes { get; } = new List<string>();
            internal int TriangleCount { get; private set; }
            internal int CurveSegmentCount { get; private set; }
            internal int Remaining => _maxPrimitives - PrimitiveHashes.Count;
            internal string Warning { get; set; }

            internal bool AddTriangle(XYZ a, XYZ b, XYZ c)
            {
                var vertices = new[] { Vertex(a), Vertex(b), Vertex(c) };
                Array.Sort(vertices, StringComparer.Ordinal);
                if (!AddPrimitive("T|" + string.Join("|", vertices))) return false;
                TriangleCount++;
                return true;
            }

            internal bool AddSegment(XYZ a, XYZ b)
            {
                var vertices = new[] { Vertex(a), Vertex(b) };
                Array.Sort(vertices, StringComparer.Ordinal);
                if (!AddPrimitive("C|" + string.Join("|", vertices))) return false;
                CurveSegmentCount++;
                return true;
            }

            internal bool AddPoint(XYZ point) => AddPrimitive("P|" + Vertex(point));

            private bool AddPrimitive(string value)
            {
                if (PrimitiveHashes.Count >= _maxPrimitives) { Warning = "geometry_primitive_limit"; return false; }
                PrimitiveHashes.Add(Hash(Encoding.UTF8.GetBytes(value)));
                return true;
            }

            private static string Vertex(XYZ point) => Quantize(point.X) + "," + Quantize(point.Y) + "," + Quantize(point.Z);
        }

        private sealed class FingerprintResult
        {
            internal FingerprintResult(bool verified, string hash, int triangles, int curves, string warning)
            { Verified = verified; Hash = hash; TriangleCount = triangles; CurveSegmentCount = curves; Warning = warning; }
            internal bool Verified { get; }
            internal string Hash { get; }
            internal int TriangleCount { get; }
            internal int CurveSegmentCount { get; }
            internal string Warning { get; }
            internal static FingerprintResult Unverified(string warning) => new FingerprintResult(false, null, 0, 0, warning);
        }
    }
}
