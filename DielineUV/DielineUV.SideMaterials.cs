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
        private static readonly Color BrownEdge = Color.FromArgb(128, 82, 47);

        private sealed class SideFinish
        {
            public bool WholeObject;
            public Color? Color;
        }

        private static SideFinish ChooseSideFinish()
        {
            var options = new[] { "沿用物体当前材质", "棕色", "自定义颜色", "整件贴图（兼容原效果）" };
            var choice = Dialogs.ShowListBox(Title, "选择侧面及非印图面的材质", options) as string;
            if (choice == null) return null;
            if (choice == options[0]) return new SideFinish();
            if (choice == options[1]) return new SideFinish { Color = BrownEdge };
            if (choice == options[3]) return new SideFinish { WholeObject = true };
            var color = BrownEdge;
            return Dialogs.ShowColorDialog(ref color) ? new SideFinish { Color = color } : null;
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

        private static bool PrepareSideMaterials(Change change, RhinoObject obj, Part part, Mesh mappingMesh,
            Canvas canvas, SideFinish finish)
        {
            if (finish.WholeObject) return true;
            var geometry = obj.Geometry;
            if (geometry is Mesh mesh)
            {
                Plane plane;
                double deviation;
                if (Plane.FitPlaneToPoints(Vertices(mesh), out plane, out deviation) == PlaneFitResult.Success &&
                    deviation <= StoredGeometryTolerance) return true;
                throw new InvalidOperationException("零件 " + part.Label + " 是立体网格，无法按面设材质。请使用多重曲面，或选择整件贴图。");
            }
            var brep = geometry is Brep original ? original.DuplicateBrep() : Brep.TryConvertBrep(geometry);
            if (brep == null) throw new InvalidOperationException("无法读取零件 " + part.Label + " 的印图面。");
            using (brep)
            {
                if (brep.Faces.Count == 1) return true;
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
                change.PrintFaces = faces;
                if (faces.Distinct().Count() == brep.Faces.Count) return true;
                change.FinishBrep = brep.DuplicateBrep();
                change.OriginalGeometry = geometry.Duplicate();
                return true;
            }
        }

        private static Guid AddArtworkMaterial(RhinoDoc doc, RenderMaterial renderMaterial, int artworkChannel)
        {
            using (var material = renderMaterial.SimulatedMaterial(RenderTexture.TextureGeneration.Allow))
            {
                if (material == null) throw new InvalidOperationException("无法建立印图面的材质。");
                using (var bitmap = material.GetBitmapTexture())
                {
                    if (bitmap == null) throw new InvalidOperationException("印图材质缺少 PNG 纹理。");
                    bitmap.MappingChannelId = artworkChannel;
                    if (!material.SetBitmapTexture(bitmap))
                        throw new InvalidOperationException("无法保存印图材质的 UV 通道。");
                }
                material.Name = renderMaterial.Name;
                int index = doc.Materials.Add(material);
                if (index < 0) throw new InvalidOperationException("无法保存印图面的材质。");
                return doc.Materials[index].Id;
            }
        }

        private static void ApplyFaceMaterials(RhinoDoc doc, Change change, Guid artworkId, SideFinish finish)
        {
            var obj = doc.Objects.FindId(change.Id);
            var current = obj.GetMaterial(true);
            bool hasOldArtwork = obj.RenderMaterial?.Name?.StartsWith("DielineUV ", StringComparison.Ordinal) == true ||
                current?.Name?.StartsWith("DielineUV ", StringComparison.Ordinal) == true;
            bool preserve = !finish.Color.HasValue && current != null && !hasOldArtwork;
            using (var material = preserve ? new Material(current) : new Material())
            {
                if (!preserve)
                {
                    material.Name = "刀模侧面";
                    material.DiffuseColor = finish.Color ?? BrownEdge;
                    if (hasOldArtwork && !finish.Color.HasValue)
                        RhinoApp.WriteLine("零件 {0} 原来是整件贴图，侧面改用棕色。", change.Id.ToString("N").Substring(0, 8));
                }
                int channel = material.MaterialChannelIndexFromId(artworkId, true);
                if (channel <= 0) throw new InvalidOperationException("无法建立逐面材质。");
                int materialIndex = doc.Materials.Add(material);
                if (materialIndex < 0) throw new InvalidOperationException("无法保存侧面材质。");
                var printFaces = new HashSet<int>(change.PrintFaces);
                var previousArtwork = new HashSet<Guid>(doc.Materials
                    .Where(entry => entry.Name?.StartsWith("DielineUV ", StringComparison.Ordinal) == true)
                    .Select(entry => entry.Id));
                foreach (var face in change.FinishBrep.Faces)
                {
                    if (printFaces.Contains(face.FaceIndex)) face.MaterialChannelIndex = channel;
                    else if (!preserve || previousArtwork.Contains(material.MaterialChannelIdFromIndex(face.MaterialChannelIndex)))
                        face.ClearMaterialChannelIndex();
                }
                if (!doc.Objects.Replace(change.Id, change.FinishBrep))
                    throw new InvalidOperationException("无法更新零件的逐面材质。");
                change.Replaced = true;
                var updated = doc.Objects.FindId(change.Id);
                var attributes = updated.Attributes.Duplicate();
                attributes.MaterialIndex = materialIndex;
                attributes.MaterialSource = ObjectMaterialSource.MaterialFromObject;
                if (!doc.Objects.ModifyAttributes(change.Id, attributes, true))
                    throw new InvalidOperationException("无法分配侧面材质。");
                var applied = doc.Objects.FindId(change.Id).GetMaterial(true);
                if (applied == null || applied.MaterialChannelIdFromIndex(channel) != artworkId)
                    throw new InvalidOperationException("Rhino 未保留印图面的材质通道。");
            }
        }
    }
}
