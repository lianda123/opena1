using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Runtime.Versioning;
using System.Globalization;
using System.Xml;
using Eto.Forms;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.PlugIns;
using Rhino.Render;
using Rhino.UI;

[assembly: AssemblyTitle("DielineUV")]
[assembly: AssemblyDescription("Apply artwork from a calibrated dieline to Rhino parts.")]
[assembly: AssemblyVersion("0.4.5.0")]
[assembly: AssemblyFileVersion("0.4.5.0")]
[assembly: Guid("6f7493dc-de9b-42cf-b1e0-1bcf1e83d1a4")]
#if LEGACY_BUILD
[assembly: TargetFramework(".NETFramework,Version=v4.8")]
#endif

namespace DielineUV
{
    public sealed class DielineUVPlugin : PlugIn { }

    [Guid("62994f85-8a4c-462d-905e-49015effa0ba")]
    public sealed partial class DielineUVCommand : Rhino.Commands.Command
    {
        private const string Title = "刀模贴图助手";
        private const string ProjectSchema = "DielineUV.project.v1";
        public override string EnglishName { get { return "DielineUV"; } }

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            try
            {
                var actions = new[] { "导出当前项目模板", "导入／更新 PNG 贴图", "展开折弯件并记录对应关系" };
                var action = Dialogs.ShowListBox(Title + " 0.4.5", "选择操作", actions) as string;
                if (action == null) return Result.Cancel;
                if (action == actions[0]) return ExportProject(doc);
                if (action == actions[1]) return ImportProject(doc);
                return PrepareFold(doc);
            }
            catch (Exception error)
            {
                RhinoApp.WriteLine("{0}：{1}", Title, error.Message);
                return Result.Failure;
            }
        }

        private static Bundle ReadBundle(string path)
        {
            Bundle bundle;
            using (var stream = File.OpenRead(path))
                bundle = (Bundle)new DataContractJsonSerializer(typeof(Bundle)).ReadObject(stream);
            if (bundle == null || bundle.Schema != ProjectSchema || bundle.Canvas == null || !bundle.Canvas.HasWorldBounds ||
                bundle.Parts == null || bundle.Parts.Length == 0 ||
                bundle.Canvas.Width <= 0 || bundle.Canvas.Height <= 0)
                throw new InvalidDataException("请选择通过“导出当前项目模板”生成、与这张 PNG 对应的 manifest.json。");
            return bundle;
        }

        private static Canvas SelectFrame(RhinoDoc doc)
        {
            if (doc.ModelUnitSystem != UnitSystem.Millimeters)
                throw new InvalidOperationException("请在对应的毫米单位模型中选择外边框。");
            while (true)
            {
                using (var picker = CreateObjectPicker("点选包住刀模的闭合矩形外边框", ObjectType.Curve))
                {
                    if (picker.Get() != GetResult.Object) return null;
                    using (var reference = picker.Object(0))
                    {
                        try { return ReadFrame(reference, doc.ModelAbsoluteTolerance); }
                        catch (InvalidDataException error) { RhinoApp.WriteLine(error.Message); }
                    }
                }
            }
        }

        private static Canvas ReadFrame(ObjRef reference, double tolerance)
        {
            var curve = reference.Curve();
            Polyline corners;
            if (curve == null || !curve.IsClosed || !curve.TryGetPolyline(out corners) || corners.Count != 5)
                throw new InvalidDataException("请选择一个闭合矩形外边框。");
            var bounds = corners.BoundingBox;
            double width = bounds.Max.X - bounds.Min.X;
            double height = bounds.Max.Y - bounds.Min.Y;
            if (width <= tolerance || height <= tolerance || bounds.Max.Z - bounds.Min.Z > tolerance)
                throw new InvalidDataException("外框需要是 XY 平面内的有效矩形。");
            var usedCorners = new HashSet<int>();
            for (int i = 0; i < 4; i++)
            {
                var edge = corners[i + 1] - corners[i];
                var p = corners[i];
                bool left = Math.Abs(p.X - bounds.Min.X) <= tolerance;
                bool bottom = Math.Abs(p.Y - bounds.Min.Y) <= tolerance;
                if ((!left && Math.Abs(p.X - bounds.Max.X) > tolerance) ||
                    (!bottom && Math.Abs(p.Y - bounds.Max.Y) > tolerance) ||
                    !usedCorners.Add((left ? 0 : 1) + (bottom ? 0 : 2)) ||
                    (Math.Abs(edge.X) > tolerance && Math.Abs(edge.Y) > tolerance))
                    throw new InvalidDataException("外框的边需要与世界 X、Y 轴平行。");
            }
            double pxPerMm = Math.Min(300.0 / 25.4, 6000.0 / Math.Max(width, height));
            return new Canvas {
                Width = Math.Max(1, (int)Math.Round(width * pxPerMm)),
                Height = Math.Max(1, (int)Math.Round(height * pxPerMm)),
                WorldMin = new[] { bounds.Min.X, bounds.Min.Y },
                WorldMax = new[] { bounds.Max.X, bounds.Max.Y },
                FrameObjectId = reference.ObjectId
            };
        }

        private static string PickFile(string title, string extension)
        {
            using (var dialog = new Eto.Forms.OpenFileDialog { Title = title })
            {
                dialog.Filters.Add(new FileFilter(extension == ".png" ? "PNG 图片" : "对应关系文件", extension));
                return dialog.ShowDialog(RhinoEtoApp.MainWindow) == DialogResult.Ok ? dialog.FileName : null;
            }
        }

        private static void WritePngTemplate(string svgPath, string output, Canvas frame)
        {
            var document = new XmlDocument { XmlResolver = null };
            document.Load(svgPath);
            var root = document.DocumentElement;
            double width = frame.WorldMax[0] - frame.WorldMin[0];
            double height = frame.WorldMax[1] - frame.WorldMin[1];
            var culture = CultureInfo.InvariantCulture;

            using (var bitmap = new Eto.Drawing.Bitmap(frame.Width, frame.Height, Eto.Drawing.PixelFormat.Format32bppRgb))
            {
                using (var graphics = new Eto.Drawing.Graphics(bitmap))
                {
                    graphics.Clear(Eto.Drawing.Colors.White);
                    foreach (XmlElement line in root.SelectNodes(".//*[local-name()='polyline']"))
                    {
                        var points = line.GetAttribute("points").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(pair => {
                                var coords = pair.Split(',');
                                return new Eto.Drawing.PointF(
                                    (float)(double.Parse(coords[0], culture) / width * frame.Width),
                                    (float)(double.Parse(coords[1], culture) / height * frame.Height));
                            }).ToArray();
                        if (points.Length > 1)
                            for (int i = 1; i < points.Length; i++)
                                graphics.DrawLine(Eto.Drawing.Colors.DimGray, points[i - 1], points[i]);
                    }
                }
                using (var png = new MemoryStream())
                {
                    bitmap.Save(png, Eto.Drawing.ImageFormat.Png);
                    SavePngWithPhysicalSize(png.ToArray(), Path.Combine(output, "template.png"), frame);
                }
            }
        }

        private static void SavePngWithPhysicalSize(byte[] png, string path, Canvas canvas)
        {
            var physical = new byte[13] { 112, 72, 89, 115, 0, 0, 0, 0, 0, 0, 0, 0, 1 };
            for (int axis = 0; axis < 2; axis++)
            {
                int pixels = axis == 0 ? canvas.Width : canvas.Height;
                double millimeters = canvas.WorldMax[axis] - canvas.WorldMin[axis];
                uint pixelsPerMeter = (uint)Math.Max(1.0, Math.Round(pixels * 1000.0 / millimeters));
                Buffer.BlockCopy(UInt32Bytes(pixelsPerMeter), 0, physical, 4 + axis * 4, 4);
            }
            uint crc = 0xffffffff;
            foreach (byte value in physical)
            {
                crc ^= value;
                for (int i = 0; i < 8; i++)
                    crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
            }
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(png, 0, 8);
                for (int offset = 8; offset < png.Length;)
                {
                    int length = checked((int)ReadUInt32(png, offset) + 12);
                    string kind = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
                    if (kind != "pHYs") writer.Write(png, offset, length);
                    if (kind == "IHDR")
                    {
                        writer.Write(UInt32Bytes(9));
                        writer.Write(physical);
                        writer.Write(UInt32Bytes(crc ^ 0xffffffff));
                    }
                    offset += length;
                }
            }
        }

        private static byte[] UInt32Bytes(uint value)
        {
            return new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
        }

        private static void ValidatePngCanvas(string path, Canvas canvas)
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
                if (!MatchesCanvas(width, height, canvas))
                    throw new InvalidDataException(string.Format(
                        "PNG 为 {0} × {1} 像素，模板为 {2} × {3} 像素，两者长宽比不同。请按同一模板的完整画板导出 PNG。分辨率可以不同。",
                        width, height, canvas.Width, canvas.Height));
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

        private static bool Apply(RhinoDoc doc, string templateDirectory, Bundle bundle, string image, SideFinish finish)
        {
            if (doc.ModelUnitSystem != UnitSystem.Millimeters)
                throw new InvalidOperationException("请打开对应的毫米单位模型。");
            string mappingPath = Path.Combine(templateDirectory, "mapping.3dm");
            if (!File.Exists(mappingPath))
                throw new InvalidDataException("找不到 mapping.3dm。请把它和同一次导出的 manifest.json 放在同一文件夹。");
            int artworkChannel = ChooseArtworkChannel(doc, bundle);
            var changes = new List<Change>();
            try
            {
                if (!PrepareChanges(doc, mappingPath, bundle, artworkChannel, finish, changes)) return false;
                ApplyPrepared(doc, changes, image, artworkChannel, finish);
                return true;
            }
            finally
            {
                foreach (var change in changes)
                {
                    change.FinishBrep?.Dispose();
                    change.OriginalGeometry?.Dispose();
                    change.Mapping?.Dispose();
                    change.OldMapping?.Dispose();
                }
            }
        }

        private static bool PrepareChanges(RhinoDoc doc, string mappingPath, Bundle bundle, int artworkChannel,
            SideFinish finish, List<Change> changes)
        {
            using (var mapFile = File3dm.Read(mappingPath))
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
                    var difference = SavedGeometryDifference(obj.Geometry, source.Geometry);
                    if (difference != null)
                        throw new InvalidOperationException("零件 " + part.Label + difference +
                            "。请确认使用本次导出的项目模板。若编辑过零件，请重新导出。");
                    var originalMesh = meshObject.Geometry as Mesh;
                    if (originalMesh == null || originalMesh.Vertices.Count == 0 ||
                        originalMesh.TextureCoordinates.Count != originalMesh.Vertices.Count)
                        throw new InvalidDataException("零件 " + part.Label + " 的 UV 数据不完整。");
                    using (var mesh = originalMesh.DuplicateMesh())
                    {
                        var mapping = TextureMapping.CreateCustomMeshMapping(mesh);
                        if (mapping == null) throw new InvalidOperationException("无法建立 UV 映射。");
                        Transform oldTransform;
                        var oldMapping = obj.GetTextureMapping(artworkChannel, out oldTransform);
                        var change = new Change { Id = id, Mapping = mapping, OldMapping = oldMapping,
                            OldTransform = oldTransform, Attributes = obj.Attributes.Duplicate() };
                        changes.Add(change);
                        if (!PrepareSideMaterials(change, obj, part, originalMesh, bundle.Canvas, finish)) return false;
                    }
                }
            }
            return true;
        }

        private static void ApplyPrepared(RhinoDoc doc, List<Change> changes, string image, int artworkChannel, SideFinish finish)
        {
            var cache = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "DielineUV", "textures");
            Directory.CreateDirectory(cache);
            var texturePath = Path.Combine(cache, Guid.NewGuid().ToString("N") + ".png");
            File.Copy(image, texturePath);
            using (var material = new Material { DiffuseColor = System.Drawing.Color.White })
            {
                var renderMaterial = RenderMaterial.CreateBasicMaterial(material, doc);
                // The document-aware SimulatedTexture constructors require Rhino 7.6.
#pragma warning disable 612, 618
                var bitmap = new SimulatedTexture { Filename = texturePath };
#pragma warning restore 612, 618
                var texture = RenderTexture.NewBitmapTexture(bitmap, doc);
                if (renderMaterial == null || texture == null)
                    throw new InvalidOperationException("无法建立图片材质。");

                var context = RenderContent.ChangeContexts.Program;
                texture.SetProjectionMode(TextureProjectionMode.MappingChannel, context);
                texture.SetMappingChannel(artworkChannel, context);
                if (texture.GetProjectionMode() != TextureProjectionMode.MappingChannel ||
                    texture.GetMappingChannel() != artworkChannel)
                    throw new InvalidOperationException("Rhino 无法设置图片的 UV 映射通道。");

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
                    if (!doc.RenderMaterials.Add(renderMaterial))
                        throw new InvalidOperationException("无法保存图片材质。");
                    Guid artworkId = changes.Any(change => change.FinishBrep != null)
                        ? AddArtworkMaterial(doc, renderMaterial, artworkChannel) : Guid.Empty;
                    foreach (var change in changes)
                    {
                        applied.Add(change);
                        if (change.FinishBrep != null)
                            ApplyFaceMaterials(doc, change, artworkId, finish);
                        else if (!doc.Objects.ModifyRenderMaterial(change.Id, renderMaterial))
                            throw new InvalidOperationException("无法更新对象材质。");
                        if (!doc.Objects.ModifyTextureMapping(change.Id, artworkChannel, change.Mapping))
                            throw new InvalidOperationException("无法设置对象贴图坐标。");
                        var target = doc.Objects.FindId(change.Id);
                        var attributes = target.Attributes.Duplicate();
                        attributes.SetUserString(ArtworkChannelKey, artworkChannel.ToString(CultureInfo.InvariantCulture));
                        if (change.PrintFacesKey != null)
                            attributes.SetUserString(change.PrintFacesKey, WriteFaceIndices(change.PrintFaces));
                        if (!doc.Objects.ModifyAttributes(change.Id, attributes, true))
                            throw new InvalidOperationException("无法保存零件的贴图设置。");
                    }
                }
                catch
                {
                    foreach (var change in Enumerable.Reverse(applied))
                    {
                        try
                        {
                            if (change.Replaced && !doc.Objects.Replace(change.Id, change.OriginalGeometry, false))
                                RhinoApp.WriteLine("恢复原件失败，请用 Undo 撤销本次操作。");
                            if (!doc.Objects.ModifyAttributes(change.Id, change.Attributes, true))
                                RhinoApp.WriteLine("恢复对象失败，请用 Undo 撤销本次操作。");
                            var obj = doc.Objects.FindId(change.Id);
                            obj.SetTextureMapping(artworkChannel, change.OldMapping, change.OldTransform);
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

        private static void ValidateFrameCoversParts(Bundle bundle)
        {
            foreach (Part part in bundle.Parts)
                for (int axis = 0; axis < 2; axis++)
                    if (part.UvMin[axis] < -0.00001 || part.UvMax[axis] > 1.00001)
                        throw new InvalidDataException("外边框没有完整包含零件 " + part.Label + " 的贴图范围。");
        }

        private sealed class Change
        {
            public Guid Id;
            public TextureMapping Mapping, OldMapping;
            public Transform OldTransform;
            public ObjectAttributes Attributes;
            public Brep FinishBrep;
            public GeometryBase OriginalGeometry;
            public int[] PrintFaces;
            public string PrintFacesKey;
            public bool Replaced;
        }
    }

    [DataContract]
    public sealed class Bundle
    {
        [DataMember(Name = "schema")] public string Schema;
        [DataMember(Name = "canvas")] public Canvas Canvas;
        [DataMember(Name = "parts")] public Part[] Parts;
    }

    [DataContract]
    public sealed class Canvas
    {
        [DataMember(Name = "width_px")] public int Width;
        [DataMember(Name = "height_px")] public int Height;
        [DataMember(Name = "world_min_xy")] public double[] WorldMin;
        [DataMember(Name = "world_max_xy")] public double[] WorldMax;
        internal Guid FrameObjectId;

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
        [DataMember(Name = "print_faces", EmitDefaultValue = false)] public int[] PrintFaces;
    }
}
