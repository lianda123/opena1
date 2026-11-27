using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Eto.Forms;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.Render;
using Rhino.UI;

namespace DielineUV
{
    public sealed partial class DielineUVCommand
    {
        private static Result ExportPortable(RhinoDoc doc)
        {
            var choices = new[] { "导出全部已贴图零件", "选择需要导出的已贴图零件",
                "导出全部零件（包含未贴图零件）", "仅导出未贴图零件" };
            var choice = Dialogs.ShowListBox(Title, "导出 GLB、OBJ 及材质；已贴图零件保留 UV，未贴图零件保留颜色", choices) as string;
            if (choice == null) return Result.Cancel;
            List<RhinoObject> objects;
            if (choice == choices[1])
            {
                using (var picker = new GetObject())
                {
                    picker.SetCommandPrompt("选择已经应用贴图的三维零件，回车确认");
                    picker.GeometryFilter = PartTypes;
                    picker.SubObjectSelect = false;
                    picker.GetMultiple(1, 0);
                    if (picker.CommandResult() != Result.Success) return picker.CommandResult();
                    objects = Enumerable.Range(0, picker.ObjectCount).Select(i => picker.Object(i).Object()).Where(o => o != null).ToList();
                }
            }
            else
            {
                var candidates = doc.Objects.GetObjectList(PartTypes);
                objects = candidates.Where(o => choice == choices[2] ||
                    (choice == choices[0] ? HasPortableArtwork(o) : !HasPortableArtwork(o))).ToList();
            }
            if (objects.Count == 0)
                throw new InvalidOperationException(choice == choices[3] ? "没有找到未贴图的三维零件。" :
                    choice == choices[2] ? "没有找到可导出的三维零件。" : "没有找到已贴图零件。请先导入项目 PNG，再使用通用导出。");
            if (choice == choices[1] && objects.Any(o => !HasPortableArtwork(o)))
                throw new InvalidOperationException("所选对象包含未绑定贴图的零件，请只选择 DielineUV 已贴图的原件。");
            if (doc.ModelUnitSystem == UnitSystem.None || doc.ModelUnitSystem == UnitSystem.Unset)
                throw new InvalidOperationException("请先设置模型单位，再导出通用模型。");
            double meters = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Meters);
            using (var dialog = new SelectFolderDialog { Title = "选择通用模型的保存位置" })
            {
                if (dialog.ShowDialog(RhinoEtoApp.MainWindow) != DialogResult.Ok) return Result.Cancel;
                string folder = ProjectExportFiles.Write(dialog.Directory, "通用模型", output =>
                {
                    var parts = new List<PortablePart>(); var materials = new List<PortableMaterial>();
                    var textures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    Directory.CreateDirectory(Path.Combine(output, "textures"));
                    foreach (var obj in objects)
                    {
                        if (!HasPortableArtwork(obj))
                        {
                            AddPortableUntextured(obj, parts, materials, textures);
                            continue;
                        }
                        int channel;
                        if (!int.TryParse(obj.Attributes.GetUserString(ArtworkChannelKey), out channel))
                            throw new InvalidDataException("零件的贴图通道记录无效：" + obj.Id);
                        using (var material = obj.GetMaterial(true))
                        {
                            if (material == null) throw new InvalidDataException("无法读取零件材质：" + obj.Id);
                            using (var bitmap = material.GetTexture(TextureType.Bitmap))
                            using (var opacity = material.GetTexture(TextureType.Transparency))
                            {
                                if (bitmap == null || !bitmap.Enabled) throw new InvalidDataException("零件缺少启用的图片材质：" + obj.Id);
                                if (!PortableChannelMatches(obj, RenderMaterial.StandardChildSlots.Diffuse, bitmap, channel) ||
                                    opacity != null && opacity.Enabled && !PortableChannelMatches(obj, RenderMaterial.StandardChildSlots.Transparency, opacity, channel))
                                    throw new InvalidDataException("零件的颜色和透明纹理未使用同一 UV 通道，请先重新应用项目 PNG：" + obj.Id);
                                if (material.Transparency > .000001)
                                    throw new InvalidDataException("零件仍为旧版整件透明材质，请先用当前版本重新导入 PNG，再导出：" + obj.Id);
                                bool hasMask = opacity != null && opacity.Enabled;
                                if (hasMask && !SameTransform(bitmap.UvwTransform, opacity.UvwTransform))
                                    throw new InvalidDataException("颜色与透明遮罩的平铺参数不同，请先重新应用 PNG：" + obj.Id);
                                string color = ResolvePortableImage(doc, bitmap.FileName);
                                string mask = hasMask ? ResolvePortableImage(doc, opacity.FileName) : null;
                                string textureKey = "image:" + color + "|" + mask + "|" + material.AlphaTransparency;
                                int materialIndex;
                                if (!textures.TryGetValue(textureKey, out materialIndex))
                                {
                                    materialIndex = materials.Count;
                                    materials.Add(WritePortableTexture(output, materialIndex, color, mask, material.AlphaTransparency));
                                    textures.Add(textureKey, materialIndex);
                                }
                                Transform objectTransform;
                                using (var mapping = obj.GetTextureMapping(channel, out objectTransform))
                                {
                                    Mesh primitive;
                                    if (mapping == null || !mapping.TryGetMappingMesh(out primitive) || primitive == null)
                                        throw new InvalidDataException("零件缺少插件的原始 UV 网格，请重新导入 PNG 后再导出：" + obj.Id);
                                    using (primitive)
                                    {
                                        if (primitive.TextureCoordinates.Count != primitive.Vertices.Count)
                                            throw new InvalidDataException("零件的逐顶点 UV 数据不完整：" + obj.Id);
                                        Transform toWorld;
                                        var toPrimitive = mapping.PrimativeTransform * objectTransform;
                                        if (!toPrimitive.TryGetInverse(out toWorld) || !primitive.Transform(toWorld))
                                            throw new InvalidDataException("无法还原零件当前的位置或方向：" + obj.Id);
                                        if (toWorld.Determinant < 0) primitive.Flip(true, true, true);
                                        var sourceBounds = obj.Geometry.GetBoundingBox(true); var exportBounds = primitive.GetBoundingBox(true);
                                        double tolerance = Math.Max(doc.ModelAbsoluteTolerance * 10, sourceBounds.Diagonal.Length * .02);
                                        if (!sourceBounds.IsValid || !exportBounds.IsValid ||
                                            sourceBounds.Min.DistanceTo(exportBounds.Min) > tolerance || sourceBounds.Max.DistanceTo(exportBounds.Max) > tolerance)
                                            throw new InvalidDataException("零件形状或位置与保存的贴图网格不一致，请重新应用 PNG：" + obj.Id);
                                        var uvTransform = bitmap.UvwTransform * mapping.UvwTransform;
                                        for (int i = 0; i < primitive.TextureCoordinates.Count; i++)
                                        {
                                            var uv = primitive.TextureCoordinates[i]; var p = uvTransform * new Point3d(uv.X, uv.Y, 0);
                                            primitive.TextureCoordinates.SetTextureCoordinate(i, p.X, p.Y);
                                        }
                                        primitive.Normals.ComputeNormals(); primitive.Compact();
                                        parts.Add(PortablePartFromMesh(obj, primitive, materialIndex, parts.Count + 1));
                                    }
                                }
                            }
                        }
                    }
                    PortableModel.WriteObj(output, parts, materials, meters);
                    using (var file = File.Create(Path.Combine(output, "model.glb"))) PortableModel.WriteGlb(file, parts, materials, meters);
                    File.WriteAllText(Path.Combine(output, "parts.json"), PortableModel.Json(PortableModel.O("schema", "DielineUV.portable.v1",
                        "source_units", doc.ModelUnitSystem.ToString(), "output_units", "meters", "up_axis", "Y",
                        "parts", parts.Select((p, i) => PortableModel.O("index", i, "source_id", p.SourceId, "name", p.Name, "material", p.Material,
                            "has_texture", materials[p.Material].HasTexture)).ToArray())), new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(output, "使用说明.txt"), PortableInstructions, new UTF8Encoding(true));
                }, new[] { "model.glb", "model.obj", "model.mtl", "parts.json", "使用说明.txt" });
                RhinoApp.WriteLine("已导出 {0} 个独立零件的标准 UV 模型：{1}", objects.Count, folder);
                RhinoApp.WriteLine("已贴图 {0} 件，未贴图 {1} 件。", objects.Count(HasPortableArtwork), objects.Count(o => !HasPortableArtwork(o)));
                RhinoApp.WriteLine("优先导入 model.glb；使用 OBJ 时请保留 model.mtl 和 textures 文件夹。导出单位为米，Y 轴向上。");
            }
            return Result.Success;
        }

        private static bool HasPortableArtwork(RhinoObject obj)
        {
            return !string.IsNullOrEmpty(obj.Attributes.GetUserString(ArtworkChannelKey));
        }

        private static void AddPortableUntextured(RhinoObject obj, List<PortablePart> parts,
            List<PortableMaterial> materials, Dictionary<string, int> materialIndices)
        {
            using (var material = obj.GetMaterial(true) ?? new Material())
            {
                var color = material.DiffuseColor;
                if (color.IsEmpty) color = System.Drawing.Color.LightGray;
                double opacity = 1 - Math.Max(0, Math.Min(1, material.Transparency));
                string key = "solid:" + color.ToArgb().ToString(CultureInfo.InvariantCulture) + "|" + opacity.ToString("R", CultureInfo.InvariantCulture);
                int materialIndex;
                if (!materialIndices.TryGetValue(key, out materialIndex))
                {
                    materialIndex = materials.Count;
                    materials.Add(new PortableMaterial { Name = "未贴图材质 " + materialIndex.ToString(CultureInfo.InvariantCulture),
                        BaseColor = new[] { PortableModel.LinearColor(color.R / 255.0), PortableModel.LinearColor(color.G / 255.0),
                            PortableModel.LinearColor(color.B / 255.0), opacity } });
                    materialIndices.Add(key, materialIndex);
                }
                using (var mesh = BuildPortableUntexturedMesh(obj))
                {
                    mesh.Normals.ComputeNormals();
                    mesh.Compact();
                    if (!mesh.IsValid || mesh.Vertices.Count == 0 || mesh.Faces.Count == 0)
                        throw new InvalidDataException("未贴图零件无法生成有效网格：" + obj.Id);
                    parts.Add(PortablePartFromMesh(obj, mesh, materialIndex, parts.Count + 1, false));
                }
            }
        }

        private static Mesh BuildPortableUntexturedMesh(RhinoObject obj)
        {
            var original = obj.Geometry as Mesh;
            if (original != null) return original.DuplicateMesh();
            var brep = obj.Geometry as Brep;
            Brep converted = null;
            Mesh[] fragments = null;
            var combined = new Mesh();
            try
            {
                if (brep == null) brep = converted = Brep.TryConvertBrep(obj.Geometry);
                fragments = brep == null ? null : Mesh.CreateFromBrep(brep, MeshingParameters.QualityRenderMesh);
                if (fragments == null || fragments.Length == 0)
                    throw new InvalidDataException("未贴图零件无法生成渲染网格：" + obj.Id);
                foreach (var fragment in fragments) if (fragment != null) combined.Append(fragment);
                return combined;
            }
            catch { combined.Dispose(); throw; }
            finally
            {
                if (fragments != null) foreach (var fragment in fragments) if (fragment != null) fragment.Dispose();
                if (converted != null) converted.Dispose();
            }
        }

        private static bool SameTransform(Transform a, Transform b)
        {
            for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) if (Math.Abs(a[i, j] - b[i, j]) > 1e-10) return false;
            return true;
        }

        private static bool PortableChannelMatches(RhinoObject obj, RenderMaterial.StandardChildSlots slot, Texture legacy, int channel)
        {
            // RDK texture settings are authoritative; legacy material simulation can expose channel 1.
            var render = obj.RenderMaterial;
            var texture = render == null ? null : render.FindChild(render.TextureChildSlotName(slot)) as RenderTexture;
            return texture != null ? texture.GetProjectionMode() == TextureProjectionMode.MappingChannel &&
                texture.GetMappingChannel() == channel : legacy.MappingChannelId == channel;
        }

        private static string ResolvePortableImage(RhinoDoc doc, string name)
        {
            if (!string.IsNullOrWhiteSpace(name) && File.Exists(name)) return Path.GetFullPath(name);
            string found; doc.Bitmaps.Find(name, true, out found);
            if (!string.IsNullOrWhiteSpace(found) && File.Exists(found)) return found;
            throw new FileNotFoundException("找不到材质图片，请先重新导入 PNG 后再导出。", name);
        }

        private static PortableMaterial WritePortableTexture(string output, int index, string colorPath, string maskPath, bool sourceAlpha)
        {
            string stem = "material_" + index.ToString("D4", CultureInfo.InvariantCulture);
            string relative = "textures/" + stem + ".png";
            string opacityRelative = null;
            byte[] embeddedImage;
            if (maskPath == null && !sourceAlpha)
            {
                byte[] original = File.ReadAllBytes(colorPath);
                if (original.Length > 26 && original[0] == 137 && original[1] == 80 && original[2] == 78 && original[3] == 71 &&
                    (original[25] == 2 || original[25] == 0))
                {
                    // Opaque atlases can be copied byte-for-byte, avoiding large bitmap allocations.
                    File.WriteAllBytes(Path.Combine(output, relative), original);
                    return new PortableMaterial { Name = stem, ColorPath = relative, Image = original, Alpha = false };
                }
            }
            using (var color = new Eto.Drawing.Bitmap(colorPath))
            using (var mask = maskPath == null ? null : new Eto.Drawing.Bitmap(maskPath))
            using (var rgba = new Eto.Drawing.Bitmap(color.Width, color.Height, Eto.Drawing.PixelFormat.Format32bppRgba))
            {
                if (mask != null && (mask.Width != color.Width || mask.Height != color.Height))
                    throw new InvalidDataException("颜色图片与透明遮罩大小不同，请重新应用 PNG。");
                using (var rgb = color.Lock())
                using (var alpha = mask == null ? null : mask.Lock())
                using (var target = rgba.Lock())
                    for (int y = 0; y < color.Height; y++) for (int x = 0; x < color.Width; x++)
                    {
                        int pixel = PortableModel.TexturePixel(rgb.GetPixel(x, y).ToArgb(),
                            alpha == null ? (int?)null : alpha.GetPixel(x, y).ToArgb(), sourceAlpha);
                        target.SetPixel(x, y, Eto.Drawing.Color.FromArgb(pixel));
                    }
                using (var png = new MemoryStream())
                {
                    rgba.Save(png, Eto.Drawing.ImageFormat.Png);
                    embeddedImage = png.ToArray();
                }
                // OBJ readers may multiply map_Kd alpha by map_d. Its color PNG must be RGB.
                // GLB instead uses the single RGBA image embedded above.
                using (var rgb = new Eto.Drawing.Bitmap(color.Width, color.Height, Eto.Drawing.PixelFormat.Format32bppRgb))
                {
                    using (var source = rgba.Lock())
                    using (var target = rgb.Lock())
                        for (int y = 0; y < color.Height; y++) for (int x = 0; x < color.Width; x++)
                        {
                            var p = source.GetPixel(x, y);
                            target.SetPixel(x, y, Eto.Drawing.Color.FromArgb(p.Rb, p.Gb, p.Bb));
                        }
                    rgb.Save(Path.Combine(output, relative), Eto.Drawing.ImageFormat.Png);
                }
                if (mask != null || sourceAlpha)
                {
                    opacityRelative = "textures/" + stem + "_opacity.png";
                    using (var opacity = new Eto.Drawing.Bitmap(color.Width, color.Height, Eto.Drawing.PixelFormat.Format32bppRgb))
                    {
                        using (var source = rgba.Lock())
                        using (var target = opacity.Lock())
                            for (int y = 0; y < color.Height; y++) for (int x = 0; x < color.Width; x++)
                            {
                                int a = source.GetPixel(x, y).Ab;
                                target.SetPixel(x, y, Eto.Drawing.Color.FromArgb(a, a, a));
                            }
                        opacity.Save(Path.Combine(output, opacityRelative), Eto.Drawing.ImageFormat.Png);
                    }
                }
            }
            return new PortableMaterial { Name = stem, ColorPath = relative, OpacityPath = opacityRelative,
                Image = embeddedImage, Alpha = opacityRelative != null };
        }

        private static PortablePart PortablePartFromMesh(RhinoObject source, Mesh mesh, int material, int sequence, bool textured = true)
        {
            int count = mesh.Vertices.Count;
            var positions = new double[count * 3]; var normals = new double[count * 3]; var uv = textured ? new double[count * 2] : null;
            for (int i = 0; i < count; i++)
            {
                var v = mesh.Vertices.Point3dAt(i); var n = (Vector3d)mesh.Normals[i]; if (!n.Unitize()) n = Vector3d.ZAxis;
                positions[3 * i] = v.X; positions[3 * i + 1] = v.Y; positions[3 * i + 2] = v.Z;
                normals[3 * i] = n.X; normals[3 * i + 1] = n.Y; normals[3 * i + 2] = n.Z;
                if (textured) { uv[2 * i] = mesh.TextureCoordinates[i].X; uv[2 * i + 1] = mesh.TextureCoordinates[i].Y; }
            }
            var triangles = new List<int>();
            foreach (var f in mesh.Faces)
            {
                triangles.AddRange(new[] { f.A, f.B, f.C });
                if (f.IsQuad) triangles.AddRange(new[] { f.A, f.C, f.D });
            }
            string name = string.IsNullOrWhiteSpace(source.Attributes.Name) ? "零件 " + sequence.ToString("D3", CultureInfo.InvariantCulture) : source.Attributes.Name;
            return new PortablePart { SourceId = source.Id.ToString(), Name = name, Positions = positions, Normals = normals,
                Uv = uv, Triangles = triangles.ToArray(), Material = material };
        }

        private const string PortableInstructions = "DielineUV 0.4.20 通用模型\r\n\r\n" +
            "优先导入 model.glb：模型、逐顶点 UV、材质和图片均在同一文件中。\r\n" +
            "不支持 GLB 的软件可导入 model.obj；必须同时保留 model.mtl 与 textures 文件夹，不能只拷贝 OBJ。\r\n" +
            "模型坐标单位为米、Y 轴向上。OBJ 导入时如需指定单位和向上轴，请按此设置。\r\n" +
            "每个零件为独立网格，源零件编号保存在 GLB 节点 extras.source_id 和 parts.json 中。\r\n" +
            "全部零件可包含未贴图零件，也可仅导出未贴图零件；它们无需先绑定，按当前材质的基本颜色和透明度导出。\r\n" +
            "未贴图零件不附加图片或虚构 UV；二维刀模线框等曲线不属于本功能的三维零件。\r\n" +
            "UV 已写入网格，通用文件不依赖 Rhino 第 32 通道。GLB 的 V 方向已按格式约定转换，勿再次翻转图片或 UV。\r\n" +
            "正面、背面和侧边共用已分区的图集；不要把它替换成原始刀模 PNG，否则位置会不匹配。\r\n" +
            "透明遮罩已合入 GLB 图片的 Alpha；OBJ 另有 map_d。不支持透明材质的软件仍可能需要开启透明显示。\r\n" +
            "此目录用于三维软件交换。绘制刀模仍使用项目模板及原始最终 PNG，不能用此图集代替刀模模板。\r\n" +
            "原 Rhino 模型、绑定关系及材质未改动。光照、反射等渲染外观取决于目标软件。\r\n";
    }
}
