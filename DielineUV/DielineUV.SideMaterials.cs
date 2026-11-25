using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Render;
using Rhino.UI;

namespace DielineUV
{
    public sealed partial class DielineUVCommand
    {
        private const string PrintFacesKey = "DielineUV.PrintFaces";
        private const string ArtworkChannelKey = "DielineUV.ArtworkChannel";
        private const string PrintingModeKey = "DielineUV.PrintingMode";
        private const string SingleFacesKey = "DielineUV.SingleFaces.";
        private const string BackMaterialKey = "DielineUV.BackMaterial";
        private const string ArtworkImageKey = "DielineUV.ArtworkImage";
        private const string SideSpecKey = "DielineUV.SideFinish";
        private const string BackSpecKey = "DielineUV.BackFinish";
        private const string AtlasImageKey = "DielineUV.AtlasImage";
        private const string FinishChannelKey = "DielineUV.FinishChannel";
        private static readonly Color BrownEdge = Color.FromArgb(128, 82, 47);

        private sealed class SideFinish
        {
            public bool WholeObject;
            public string Printing = "keep";
            public FinishSpec Sides;
            public FinishSpec Back;
            public double? SingleSideSign;
            public TextureAtlas Atlas;
        }

        private sealed class FinishSpec
        {
            public string Kind = "current";
            public Color Color;
            public string Texture;
        }

        private static int ChooseArtworkChannel(RhinoDoc doc, Bundle bundle)
        {
            var objects = bundle.Parts.Select(part => doc.Objects.FindId(Guid.Parse(part.SourceId)))
                .Where(obj => obj != null).ToArray();
            for (int channel = 32; channel <= 255; channel++)
            {
                string value = channel.ToString(CultureInfo.InvariantCulture);
                if (objects.All(obj => obj.Attributes.GetUserString(ArtworkChannelKey) == value ||
                    obj.GetTextureMapping(channel) == null))
                    return channel;
            }
            throw new InvalidOperationException("找不到空闲的贴图映射通道。");
        }

        private static int[] FlatPrintFaces(GeometryBase geometry, Transform toLayout)
        {
            Transform inverse;
            if (!toLayout.TryGetInverse(out inverse)) return null;
            return FlatPrintFaces(geometry, Vector3d.CrossProduct(inverse * Vector3d.XAxis, inverse * Vector3d.YAxis));
        }

        private static int[] FlatPrintFaces(GeometryBase geometry, Vector3d normal)
        {
            if (!normal.Unitize() || geometry is Mesh) return null;
            var original = geometry as Brep;
            using (var converted = original == null ? Brep.TryConvertBrep(geometry) : null)
            {
                var brep = original ?? converted;
                if (brep == null) return null;
                var faces = new List<int>();
                foreach (var face in brep.Faces)
                {
                    Plane plane;
                    if (face.TryGetPlane(out plane, StoredGeometryTolerance) &&
                        Math.Abs(plane.ZAxis * normal) >= 0.999999)
                        faces.Add(face.FaceIndex);
                }
                return faces.Count == 0 ? null : faces.ToArray();
            }
        }

        // Older templates only saved UV vertices. Check that they describe one
        // planar projection before inferring the two broad faces of a flat part.
        private static bool TryReadProjectionNormal(Mesh mesh, Canvas canvas, out Vector3d normal)
        {
            normal = Vector3d.Unset;
            double largest = 0;
            MeshFace candidate = default(MeshFace);
            foreach (var face in mesh.Faces)
            {
                var a = mesh.TextureCoordinates[face.A];
                var b = mesh.TextureCoordinates[face.B];
                var c = mesh.TextureCoordinates[face.C];
                double area = Math.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X));
                if (area <= largest) continue;
                candidate = face;
                largest = area;
            }
            if (largest <= 1e-12) return false;
            var origin = mesh.Vertices.Point3dAt(candidate.A);
            var x = mesh.Vertices.Point3dAt(candidate.B) - origin;
            var y = mesh.Vertices.Point3dAt(candidate.C) - origin;
            double xx = x * x, xy = x * y, yy = y * y;
            double determinant = xx * yy - xy * xy;
            if (determinant <= 1e-16) return false;
            var uv0 = mesh.TextureCoordinates[candidate.A];
            var uv1 = mesh.TextureCoordinates[candidate.B];
            var uv2 = mesh.TextureCoordinates[candidate.C];
            double width = canvas.WorldMax[0] - canvas.WorldMin[0];
            double height = canvas.WorldMax[1] - canvas.WorldMin[1];
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                var p = mesh.Vertices.Point3dAt(i) - origin;
                double px = p * x, py = p * y;
                double u = (px * yy - py * xy) / determinant;
                double v = (py * xx - px * xy) / determinant;
                var uv = mesh.TextureCoordinates[i];
                if (Math.Abs(uv.X - uv0.X - u * (uv1.X - uv0.X) - v * (uv2.X - uv0.X)) * width > 0.03 ||
                    Math.Abs(uv.Y - uv0.Y - u * (uv1.Y - uv0.Y) - v * (uv2.Y - uv0.Y)) * height > 0.03)
                    return false;
            }
            normal = Vector3d.CrossProduct(x, y);
            return normal.Unitize();
        }

        private static int[] ReadFaceIndices(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var indices = new List<int>();
            foreach (string token in text.Split(','))
            {
                int index;
                if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out index)) return null;
                indices.Add(index);
            }
            return indices.Distinct().ToArray();
        }

        private static string WriteFaceIndices(IEnumerable<int> indices)
        {
            return string.Join(",", indices.Select(index => index.ToString(CultureInfo.InvariantCulture)));
        }

        private static bool PrepareSideMaterials(RhinoDoc doc, Change change, RhinoObject obj, Part part, Mesh mappingMesh,
            Canvas canvas, SideFinish finish)
        {
            if (finish.WholeObject)
            {
                var existing = obj.Geometry as Brep;
                if (existing != null && existing.Faces.Any(face => face.MaterialChannelIndex > 0))
                {
                    change.FinishBrep = existing.DuplicateBrep();
                    change.OriginalGeometry = obj.Geometry.Duplicate();
                    foreach (var face in change.FinishBrep.Faces) face.ClearMaterialChannelIndex();
                }
                return true;
            }
            var geometry = obj.Geometry;
            if (geometry is Mesh mesh)
            {
                Plane plane;
                double deviation;
                if (Plane.FitPlaneToPoints(Vertices(mesh), out plane, out deviation) == PlaneFitResult.Success &&
                    deviation <= StoredGeometryTolerance)
                {
                    if (finish.Printing == "single" || finish.Printing == "reselect")
                        throw new InvalidOperationException("零件 " + part.Label + " 是零厚度网格，不能分别指定实体正反面材质。");
                    return true;
                }
                throw new InvalidOperationException("零件 " + part.Label + " 是立体网格，无法按面设材质。请使用多重曲面，或选择整件贴图。");
            }
            var brep = geometry is Brep original ? original.DuplicateBrep() : Brep.TryConvertBrep(geometry);
            if (brep == null) throw new InvalidOperationException("无法读取零件 " + part.Label + " 的印图面。");
            using (brep)
            {
                if (brep.Faces.Count == 1)
                {
                    if (finish.Printing == "single" || finish.Printing == "reselect")
                        throw new InvalidOperationException("零件 " + part.Label + " 没有独立的厚度和背面，不能分别设置正反材质。请使用带厚度的实体。");
                    return true;
                }
                string key = PrintFacesKey + "." + part.MappingId;
                var faces = part.PrintFaces ?? ReadFaceIndices(obj.Attributes.GetUserString(key));
                Vector3d normal;
                if (faces == null && TryReadProjectionNormal(mappingMesh, canvas, out normal))
                    faces = FlatPrintFaces(brep, normal);
                if (faces == null)
                {
                    RhinoApp.WriteLine("旧模板未记录零件 {0} 的印图面。请选择原件上要贴 PNG 的面。", part.Label);
                    faces = SelectFoldFaces(obj);
                    if (faces == null) return false;
                    change.PrintFacesKey = key;
                }
                if (faces.Length == 0 || faces.Any(index => index < 0 || index >= brep.Faces.Count))
                    throw new InvalidDataException("零件 " + part.Label + " 的印图面编号无效，请重新导出模板。");
                change.PrintFacesKey = key;
                change.AllPrintFaces = faces;
                if (!PreparePrintingFaces(doc, change, obj, part, brep, faces, mappingMesh, canvas, finish)) return false;
                if (change.AllPrintFaces.Distinct().Count() == brep.Faces.Count && !change.SingleSided) return true;
                change.FinishBrep = brep.DuplicateBrep();
                change.OriginalGeometry = geometry.Duplicate();
                return true;
            }
        }

        private static bool RestoreOriginalGeometry(RhinoDoc doc, Change change)
        {
            if (change.OriginalGeometry is Brep brep) return doc.Objects.Replace(change.Id, brep);
            if (change.OriginalGeometry is Extrusion extrusion) return doc.Objects.Replace(change.Id, extrusion);
            if (change.OriginalGeometry is Surface surface) return doc.Objects.Replace(change.Id, surface);
            return false;
        }
    }
}
