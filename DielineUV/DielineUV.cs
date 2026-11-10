using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Runtime.Versioning;
using Eto.Forms;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.PlugIns;
using Rhino.Render;
using Rhino.UI;

[assembly: AssemblyTitle("DielineUV")]
[assembly: AssemblyDescription("Apply artwork from a calibrated dieline to Rhino parts.")]
[assembly: AssemblyVersion("0.2.1.0")]
[assembly: AssemblyFileVersion("0.2.1.0")]
[assembly: Guid("6f7493dc-de9b-42cf-b1e0-1bcf1e83d1a4")]
#if LEGACY_BUILD
[assembly: TargetFramework(".NETFramework,Version=v4.8")]
#endif

namespace DielineUV
{
    public sealed class DielineUVPlugin : PlugIn { }

    [Guid("62994f85-8a4c-462d-905e-49015effa0ba")]
    public sealed class DielineUVCommand : Rhino.Commands.Command
    {
        private const string Title = "刀模贴图助手";
        private const int Channel = 1;
        public override string EnglishName { get { return "DielineUV"; } }

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            try
            {
                var actions = new[] { "导出绘图模板", "应用校准图", "导入／更新 PNG 贴图" };
                var action = Dialogs.ShowListBox(Title + " 0.2.1", "选择操作", actions) as string;
                if (action == null) return Result.Cancel;

                string manifestPath = Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location),
                    "ces-sample", "manifest.json");
                if (!File.Exists(manifestPath))
                    manifestPath = PickFile("选择贴图样例的 manifest.json", ".json");
                if (manifestPath == null) return Result.Cancel;
                string sample = Path.GetDirectoryName(manifestPath);
                Bundle bundle;
                using (var stream = File.OpenRead(manifestPath))
                    bundle = (Bundle)new DataContractJsonSerializer(typeof(Bundle)).ReadObject(stream);
                if (bundle == null || bundle.Schema != "DielineUV.sample.v1" || bundle.Canvas == null ||
                    bundle.Parts == null || bundle.Parts.Length == 0 ||
                    bundle.Canvas.Width <= 0 || bundle.Canvas.Height <= 0)
                    throw new InvalidDataException("样例对应关系文件无效。");

                if (action == actions[0]) return Export(sample);

                string path = Path.Combine(sample, "calibration.png");
                int flip = 0;
                if (action == actions[2])
                {
                    path = PickFile("选择整张 PNG 贴图", ".png");
                    if (path == null) return Result.Cancel;
                    var directions = new[] { "使用校准方向", "长度反向", "宽度反向", "双向反转" };
                    var direction = Dialogs.ShowListBox(Title, "选择 C 弯曲件方向", directions) as string;
                    if (direction == null) return Result.Cancel;
                    flip = Array.IndexOf(directions, direction);
                }
                Canvas imageCanvas = MatchPngCanvas(path, bundle);
                Apply(doc, sample, bundle, imageCanvas, path, flip);
                RhinoApp.WriteLine("已为 {0} 个零件更新贴图。切换到渲染显示模式查看。", bundle.Parts.Length);
                return Result.Success;
            }
            catch (Exception error)
            {
                RhinoApp.WriteLine("{0}：{1}", Title, error.Message);
                return Result.Failure;
            }
        }

        private static string PickFile(string title, string extension)
        {
            using (var dialog = new Eto.Forms.OpenFileDialog { Title = title })
            {
                dialog.Filters.Add(new FileFilter(extension == ".png" ? "PNG 图片" : "对应关系文件", extension));
                return dialog.ShowDialog(RhinoEtoApp.MainWindow) == DialogResult.Ok ? dialog.FileName : null;
            }
        }

        private static Result Export(string sample)
        {
            using (var dialog = new SelectFolderDialog { Title = "选择绘图模板的保存位置" })
            {
                if (dialog.ShowDialog(RhinoEtoApp.MainWindow) != DialogResult.Ok) return Result.Cancel;
                string output = Path.Combine(dialog.Directory, "DielineUV_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(output);
                foreach (string name in new[] { "template.png", "template.svg", "calibration.png", "manifest.json", "mapping.3dm" })
                    File.Copy(Path.Combine(sample, name), Path.Combine(output, name));
                foreach (string name in new[] { "template-A3.png", "template-A3.svg", "calibration-A3.png" })
                    if (File.Exists(Path.Combine(sample, name)))
                        File.Copy(Path.Combine(sample, name), Path.Combine(output, name));
                RhinoApp.WriteLine("绘图模板已导出到 {0}", output);
                return Result.Success;
            }
        }

        private static Canvas MatchPngCanvas(string path, Bundle bundle)
        {
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                byte[] header = reader.ReadBytes(24);
                byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
                if (header.Length != 24 || !signature.SequenceEqual(header.Take(8)) ||
                    System.Text.Encoding.ASCII.GetString(header, 12, 4) != "IHDR")
                    throw new InvalidDataException("请选择 PNG 图片。");
                uint width = ReadUInt32(header, 16);
                uint height = ReadUInt32(header, 20);
                if (MatchesCanvas(width, height, bundle.Canvas)) return bundle.Canvas;
                if (MatchesCanvas(width, height, bundle.A3Canvas)) return bundle.A3Canvas;
                string expected = string.Format("裁切模板 {0} × {1}", bundle.Canvas.Width, bundle.Canvas.Height);
                if (bundle.A3Canvas != null)
                    expected += string.Format("，横向 A3 模板 {0} × {1}", bundle.A3Canvas.Width, bundle.A3Canvas.Height);
                throw new InvalidDataException(string.Format(
                    "PNG 为 {0} × {1} 像素。支持{2}的比例。请用对应模板的完整画板导出。",
                    width, height, expected));
            }
        }

        private static bool MatchesCanvas(uint width, uint height, Canvas canvas)
        {
            if (canvas == null || canvas.Width <= 0 || canvas.Height <= 0 || width == 0 || height == 0)
                return false;
            double expectedWidth = height * ((double)canvas.Width / canvas.Height);
            // Allow raster rounding and small differences between vector and pixel canvas sizes.
            return Math.Abs(width - expectedWidth) <= Math.Max(2.0, expectedWidth * 0.0025);
        }

        private static uint ReadUInt32(byte[] buffer, int start)
        {
            return ((uint)buffer[start] << 24) | ((uint)buffer[start + 1] << 16) |
                ((uint)buffer[start + 2] << 8) | buffer[start + 3];
        }

        private static void Apply(RhinoDoc doc, string sample, Bundle bundle, Canvas imageCanvas, string image, int flip)
        {
            if (doc.ModelUnitSystem != UnitSystem.Millimeters)
                throw new InvalidOperationException("请打开对应的毫米单位模型。");
            bool remapCanvas = imageCanvas != bundle.Canvas;
            if (remapCanvas && (!bundle.Canvas.HasWorldBounds || !imageCanvas.HasWorldBounds))
                throw new InvalidDataException("样例缺少 A3 画板的定位范围。");
            var changes = new List<Change>();
            using (var mapFile = File3dm.Read(Path.Combine(sample, "mapping.3dm")))
            {
                if (mapFile == null) throw new InvalidDataException("无法读取 mapping.3dm。");
                var mapObjects = mapFile.Objects.ToDictionary(o => o.Attributes.ObjectId);
                var sourceIds = new HashSet<Guid>();
                foreach (var part in bundle.Parts)
                {
                    Guid id = Guid.Parse(part.SourceId);
                    RhinoObject obj = doc.Objects.FindId(id);
                    if (!sourceIds.Add(id) || obj == null || obj.IsLocked || obj.IsReference)
                        throw new InvalidOperationException("无法修改零件 " + part.Label + "。请检查模型、锁定状态和对应关系。");
                    File3dmObject source;
                    File3dmObject meshObject;
                    if (!mapObjects.TryGetValue(id, out source) ||
                        !mapObjects.TryGetValue(Guid.Parse(part.MappingId), out meshObject))
                        throw new InvalidDataException("缺少零件 " + part.Label + " 的映射数据。");
                    if (!GeometryBase.GeometryEquals(obj.Geometry, source.Geometry))
                        throw new InvalidOperationException("零件 " + part.Label + " 的形状或位置已改变，需要重新校准。");
                    var originalMesh = meshObject.Geometry as Mesh;
                    if (originalMesh == null || originalMesh.TextureCoordinates.Count != originalMesh.Vertices.Count)
                        throw new InvalidDataException("零件 " + part.Label + " 的 UV 数据不完整。");
                    using (var mesh = originalMesh.DuplicateMesh())
                    {
                        bool flipPart = part.Label == "C" && flip > 0;
                        if (flipPart || remapCanvas)
                            for (int index = 0; index < mesh.TextureCoordinates.Count; index++)
                            {
                                var uv = mesh.TextureCoordinates[index];
                                double u = flipPart && (flip & 2) != 0 ? part.UvMin[0] + part.UvMax[0] - uv.X : uv.X;
                                double v = flipPart && (flip & 1) != 0 ? part.UvMin[1] + part.UvMax[1] - uv.Y : uv.Y;
                                if (remapCanvas)
                                {
                                    u = TranslateCanvasCoordinate(u, 0, bundle.Canvas, imageCanvas);
                                    v = TranslateCanvasCoordinate(v, 1, bundle.Canvas, imageCanvas);
                                }
                                mesh.TextureCoordinates.SetTextureCoordinate(index, u, v);
                            }
                        var mapping = TextureMapping.CreateCustomMeshMapping(mesh);
                        if (mapping == null) throw new InvalidOperationException("无法建立 UV 映射。");
                        Transform oldTransform;
                        var oldMapping = obj.GetTextureMapping(Channel, out oldTransform);
                        changes.Add(new Change { Id = id, Mapping = mapping, OldMapping = oldMapping,
                            OldTransform = oldTransform, Attributes = obj.Attributes.Duplicate() });
                    }
                }
            }

            var cache = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "DielineUV", "textures");
            Directory.CreateDirectory(cache);
            var texturePath = Path.Combine(cache, Guid.NewGuid().ToString("N") + ".png");
            File.Copy(image, texturePath);
            using (var material = new Material { DiffuseColor = System.Drawing.Color.White })
            {
                var renderMaterial = RenderMaterial.CreateBasicMaterial(material, doc);
                // The document-aware SimulatedTexture constructors require Rhino 7.6.
#pragma warning disable 618
                var bitmap = new SimulatedTexture { Filename = texturePath };
#pragma warning restore 618
                var texture = RenderTexture.NewBitmapTexture(bitmap, doc);
                if (renderMaterial == null || texture == null)
                    throw new InvalidOperationException("无法建立图片材质。");

                var context = RenderContent.ChangeContexts.Program;
                texture.SetProjectionMode(TextureProjectionMode.MappingChannel, context);
                texture.SetMappingChannel(Channel, context);
                if (texture.GetProjectionMode() != TextureProjectionMode.MappingChannel ||
                    texture.GetMappingChannel() != Channel)
                    throw new InvalidOperationException("Rhino 无法将图片纹理设为 UV 通道 1。");

                renderMaterial.Name = "DielineUV " + Path.GetFileNameWithoutExtension(texturePath).Substring(0, 8);
                var slot = renderMaterial.TextureChildSlotName(RenderMaterial.StandardChildSlots.Diffuse);
                if (!renderMaterial.SetChild(texture, slot))
                    throw new InvalidOperationException("无法将图片添加到材质的颜色纹理。");
                renderMaterial.SetChildSlotOn(slot, true, context);
                renderMaterial.SetChildSlotAmount(slot, 100.0, context);
                uint undo = doc.BeginUndoRecord("DielineUV");
                var applied = new List<Change>();
                try
                {
                    doc.RenderMaterials.Add(renderMaterial);
                    foreach (var change in changes)
                    {
                        applied.Add(change);
                        if (!doc.Objects.ModifyTextureMapping(change.Id, Channel, change.Mapping))
                            throw new InvalidOperationException("无法设置对象贴图坐标。");
                        var target = doc.Objects.FindId(change.Id);
                        target.RenderMaterial = renderMaterial;
                        target.CommitChanges();
                        var assigned = doc.Objects.FindId(change.Id).RenderMaterial;
                        if (assigned == null || assigned.Id != renderMaterial.Id)
                            throw new InvalidOperationException("无法更新对象材质。");
                    }
                }
                catch
                {
                    foreach (var change in Enumerable.Reverse(applied))
                    {
                        try
                        {
                            var obj = doc.Objects.FindId(change.Id);
                            obj.SetTextureMapping(Channel, change.OldMapping, change.OldTransform);
                            if (!doc.Objects.ModifyAttributes(change.Id, change.Attributes, true))
                                RhinoApp.WriteLine("恢复对象失败，请用 Undo 撤销本次操作。");
                        }
                        catch { RhinoApp.WriteLine("恢复对象失败，请用 Undo 撤销本次操作。"); }
                    }
                    throw;
                }
                finally
                {
                    if (undo != 0) doc.EndUndoRecord(undo);
                    doc.Views.Redraw();
                }
            }
        }

        private static double TranslateCanvasCoordinate(double uv, int axis, Canvas source, Canvas target)
        {
            double world = source.WorldMin[axis] + uv * (source.WorldMax[axis] - source.WorldMin[axis]);
            return (world - target.WorldMin[axis]) / (target.WorldMax[axis] - target.WorldMin[axis]);
        }

        private sealed class Change
        {
            public Guid Id;
            public TextureMapping Mapping, OldMapping;
            public Transform OldTransform;
            public ObjectAttributes Attributes;
        }
    }

    [DataContract]
    public sealed class Bundle
    {
        [DataMember(Name = "schema")] public string Schema;
        [DataMember(Name = "canvas")] public Canvas Canvas;
        [DataMember(Name = "a3_canvas")] public Canvas A3Canvas;
        [DataMember(Name = "parts")] public Part[] Parts;
    }

    [DataContract]
    public sealed class Canvas
    {
        [DataMember(Name = "width_px")] public int Width;
        [DataMember(Name = "height_px")] public int Height;
        [DataMember(Name = "world_min_xy")] public double[] WorldMin;
        [DataMember(Name = "world_max_xy")] public double[] WorldMax;

        public bool HasWorldBounds
        {
            get
            {
                return WorldMin != null && WorldMax != null && WorldMin.Length == 2 && WorldMax.Length == 2 &&
                    WorldMax[0] > WorldMin[0] && WorldMax[1] > WorldMin[1];
            }
        }
    }

    [DataContract]
    public sealed class Part
    {
        [DataMember(Name = "label")] public string Label;
        [DataMember(Name = "source_id")] public string SourceId;
        [DataMember(Name = "mapping_id")] public string MappingId;
        [DataMember(Name = "uv_min")] public double[] UvMin;
        [DataMember(Name = "uv_max")] public double[] UvMax;
    }
}
