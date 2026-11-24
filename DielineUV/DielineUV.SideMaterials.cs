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
            return Dialogs.ShowColorDialog(ref color)
                ? new SideFinish { Color = Color.FromArgb(color.R, color.G, color.B) }
                : null;
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

        private static Guid AddArtworkMaterial(RhinoDoc doc, RenderMaterial renderMaterial,
            string texturePath, int artworkChannel)
        {
            // Use the same registered RDK texture as whole-object mode. The
            // material channel holds an ON_Material id, whose RDK id must also
            // be retained for Rendered view and Rhino Render.
            var simulated = renderMaterial.SimulatedMaterial(RenderTexture.TextureGeneration.Allow);
            if (simulated == null) throw new InvalidOperationException("无法生成印图面的图片材质。");
            using (var material = new Material(simulated))
            {
                material.Name = renderMaterial.Name + " 印图面";
                material.RenderMaterialInstanceId = renderMaterial.Id;
#pragma warning disable 612, 618
                using (var source = new SimulatedTexture { Filename = texturePath })
#pragma warning restore 612, 618
                {
                    // Keep the ON_Material preview in sync with the RDK texture,
                    // avoiding a baked simulation that uses another mapping channel.
                    source.SetMappingChannelAndProjectionMode(SimulatedTexture.ProjectionModes.MappingChannel,
                        artworkChannel, SimulatedTexture.EnvironmentMappingModes.Automatic);
                    using (var bitmap = source.Texture())
                    {
                        if (bitmap == null || bitmap.MappingChannelId != artworkChannel ||
                            !material.SetTexture(bitmap, TextureType.Bitmap))
                            throw new InvalidOperationException("图片材质未保留原 PNG 和 UV 通道。");
                    }
                }
                int index = doc.Materials.Add(material);
                if (index < 0) throw new InvalidOperationException("无法保存印图面的材质。");
                return doc.Materials[index].Id;
            }
        }

        private static bool RestoreOriginalGeometry(RhinoDoc doc, Change change)
        {
            // The generic GeometryBase overload is absent in early Rhino 7.
            if (change.OriginalGeometry is Brep brep) return doc.Objects.Replace(change.Id, brep);
            if (change.OriginalGeometry is Extrusion extrusion) return doc.Objects.Replace(change.Id, extrusion);
            if (change.OriginalGeometry is Surface surface) return doc.Objects.Replace(change.Id, surface);
            return false;
        }

        private static void PrepareFaceMaterials(RhinoDoc doc, Change change, Guid artworkId, string artworkName,
            SideFinish finish, ref int? solidSideMaterialIndex)
        {
            var obj = doc.Objects.FindId(change.Id);
            var current = finish.Color.HasValue ? null : obj.GetMaterial(true);
            bool hasOldArtwork = current != null &&
                (IsArtworkMaterialName(current.Name) || IsArtworkMaterialName(obj.RenderMaterial?.Name));
            bool preserve = current != null && !hasOldArtwork;
            int materialIndex;
            if (preserve)
            {
                using (var copy = new Material(current))
                    materialIndex = RegisterSideMaterial(doc, copy, artworkId);
            }
            else
            {
                if (!solidSideMaterialIndex.HasValue)
                {
                    var color = finish.Color ?? BrownEdge;
                    using (var solid = new Material
                    {
                        Name = string.Format("刀模侧面 #{0:X2}{1:X2}{2:X2} ({3})", color.R, color.G, color.B, artworkName),
                        DiffuseColor = color
                    })
                        solidSideMaterialIndex = RegisterSideMaterial(doc, solid, artworkId);
                }
                materialIndex = solidSideMaterialIndex.Value;
                if (hasOldArtwork)
                    RhinoApp.WriteLine("零件 {0} 原来是整件贴图，侧面改用棕色。", change.Label);
            }

            var material = doc.Materials[materialIndex];
            int channel = material.MaterialChannelIndexFromId(artworkId, false);
            if (channel <= 0) throw new InvalidOperationException("材质表未保留零件 " + change.Label + " 的印图通道。");
            var printFaces = new HashSet<int>(change.PrintFaces);
            var previousArtwork = preserve ? new HashSet<Guid>(doc.Materials
                .Where(entry => IsArtworkMaterialName(entry.Name)).Select(entry => entry.Id)) : null;
            foreach (var face in change.FinishBrep.Faces)
            {
                if (printFaces.Contains(face.FaceIndex)) face.MaterialChannelIndex = channel;
                else if (!preserve || previousArtwork.Contains(material.MaterialChannelIdFromIndex(face.MaterialChannelIndex)))
                    face.ClearMaterialChannelIndex();
            }

            change.SideMaterialIndex = materialIndex;
            change.SideColor = preserve ? (Color?)null : finish.Color ?? BrownEdge;

            // Clear a previous object-level RDK assignment through the RDK API.
            // Clearing MaterialRefs alone does not clear that assignment.
            var updated = doc.Objects.FindId(change.Id);
            if (updated.RenderMaterial != null)
                doc.Objects.ModifyRenderMaterial(change.Id, null);
        }

        private static bool IsArtworkMaterialName(string name)
        {
            return name?.StartsWith("DielineUV ", StringComparison.Ordinal) == true;
        }

        private static int RegisterSideMaterial(RhinoDoc doc, Material material, Guid artworkId)
        {
            if (material.MaterialChannelIndexFromId(artworkId, true) <= 0)
                throw new InvalidOperationException("无法建立逐面材质。");
            int index = doc.Materials.Add(material);
            if (index < 0) throw new InvalidOperationException("无法保存侧面材质。");
            return index;
        }

        private static void VerifyFaceMaterials(RhinoDoc doc, Change change, Guid renderMaterialId,
            string texturePath, int artworkChannel)
        {
            var obj = doc.Objects.FindId(change.Id);
            var brep = obj?.Geometry as Brep;
            var material = obj?.GetMaterial(true);
            if (brep == null || material == null)
                throw new InvalidOperationException("无法读取零件 " + change.Label + " 写入后的逐面材质。");
            var printFaces = new HashSet<int>(change.PrintFaces);
            foreach (var face in brep.Faces)
            {
                // Rhino can remap material table indices. Check the material actually
                // resolved for the face instead of requiring the original table index.
                var resolved = obj.GetMaterial(new ComponentIndex(ComponentIndexType.BrepFace, face.FaceIndex));
                if (resolved == null)
                    throw new InvalidOperationException("无法读取零件 " + change.Label + " 的面材质。");
                using (var bitmap = resolved.GetTexture(TextureType.Bitmap))
                {
                    if (printFaces.Contains(face.FaceIndex))
                    {
                        if (resolved.RenderMaterialInstanceId != renderMaterialId ||
                            bitmap == null || bitmap.MappingChannelId != artworkChannel ||
                            !SameTextureFile(bitmap.FileName, texturePath))
                            throw new InvalidOperationException("零件 " + change.Label + " 的印图面未取得 PNG 和 UV 通道（面 " +
                                (face.FaceIndex + 1) + "，物体材质编号 " + obj.Attributes.MaterialIndex + "）。");
                    }
                    else if (change.SideColor.HasValue &&
                        (resolved.DiffuseColor.ToArgb() != change.SideColor.Value.ToArgb() || bitmap != null))
                        throw new InvalidOperationException("零件 " + change.Label + " 的侧面颜色未正确写入（面 " +
                            (face.FaceIndex + 1) + "，物体材质编号 " + obj.Attributes.MaterialIndex + "）。");
                }
            }
        }

        private static bool SameTextureFile(string actual, string expected)
        {
            if (string.IsNullOrWhiteSpace(actual)) return false;
            return string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected),
                Rhino.Runtime.HostUtils.RunningOnWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        private static bool CommitObjectAttributes(RhinoDoc doc, Guid id, ObjectAttributes attributes)
        {
            var obj = doc.Objects.FindId(id);
            if (obj == null) return false;
            if (SameAttributes(obj.Attributes, attributes)) return true;
            // Commit via a fresh RhinoObject so geometry replacements cannot leave
            // a previously cached attributes wrapper in charge of the new object.
            obj.Attributes = attributes;
            if (obj.CommitChanges()) return true;
            var saved = doc.Objects.FindId(id);
            return saved != null && SameAttributes(saved.Attributes, attributes);
        }

        private static bool SameAttributes(ObjectAttributes a, ObjectAttributes b)
        {
            var options = new Rhino.FileIO.SerializationOptions { RhinoVersion = 7, WriteUserData = true };
            return a.ToJSON(options) == b.ToJSON(options);
        }

        private static void RestoreChange(RhinoDoc doc, Change change, int artworkChannel)
        {
            if (change.Replaced && !RestoreOriginalGeometry(doc, change))
                throw new InvalidOperationException("恢复原几何失败");
            var obj = doc.Objects.FindId(change.Id);
            if (obj == null) throw new InvalidOperationException("找不到原件");
            // Restore only the modified mapping channel, including its original transform.
            using (var current = obj.GetTextureMapping(artworkChannel))
            {
                if (current != null || change.OldMapping != null)
                    obj.SetTextureMapping(artworkChannel, change.OldMapping, change.OldTransform);
            }
            if (!CommitObjectAttributes(doc, change.Id, change.Attributes))
                throw new InvalidOperationException("恢复原属性失败");
            obj = doc.Objects.FindId(change.Id);
            Transform transform;
            using (var restored = obj.GetTextureMapping(artworkChannel, out transform))
            {
                if (change.OldMapping == null ? restored != null :
                    restored == null || restored.Id != change.OldMapping.Id || transform != change.OldTransform)
                    throw new InvalidOperationException("恢复原 UV 通道失败");
            }
        }
    }
}
