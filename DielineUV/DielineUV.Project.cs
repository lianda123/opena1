using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Xml;
using Eto.Forms;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.UI;

namespace DielineUV
{
    public sealed partial class DielineUVCommand
    {
        private const string LayoutRole = "WoodSheetLayoutRole";
        private const string LayoutSource = "WoodSheetLayoutSourceId";
        private const string LayoutTransform = "WoodSheetLayoutSourceToFlatTransform";
        private const ObjectType PartTypes = ObjectType.Brep | ObjectType.Extrusion | ObjectType.Surface | ObjectType.Mesh;

        private sealed class ProjectPart
        {
            public RhinoObject Source;
            public string Label;
            public Transform ToLayout;
            public Curve[] Guides;
            public Mesh MappingMesh;
            public Point3d[] LayoutPoints;
        }

        private static Result ExportProject(RhinoDoc doc)
        {
            var frame = SelectFrame(doc);
            if (frame == null) return Result.Cancel;
            var options = new[] { "识别排版关联", "手动绑定平板（每件三点）" };
            var option = Dialogs.ShowListBox(Title, "选择当前外框内零件的匹配方式", options) as string;
            if (option == null) return Result.Cancel;
            double tolerance = Math.Max(doc.ModelAbsoluteTolerance * 4.0, 0.02);
            var parts = option == options[0] ? FindLayoutParts(doc, frame, tolerance) :
                BindParts(doc, frame, tolerance);
            if (parts.Count == 0)
                throw new InvalidOperationException("本外框内没有可用关联。请选排版件所在的外框，或使用“手动绑定平板”。");
            try
            {
                using (var dialog = new SelectFolderDialog { Title = "选择项目贴图模板的保存位置" })
                {
                    if (dialog.ShowDialog(RhinoEtoApp.MainWindow) != DialogResult.Ok) return Result.Cancel;
                    string output = Path.Combine(dialog.Directory, "DielineUV_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    Directory.CreateDirectory(output);
                    WriteProject(parts, frame, output);
                    RhinoApp.WriteLine("已为当前项目的 {0} 个零件生成贴图模板：{1}", parts.Count, output);
                    return Result.Success;
                }
            }
            finally { foreach (var part in parts) if (part.MappingMesh != null) part.MappingMesh.Dispose(); }
        }

        private static Result ImportProject(RhinoDoc doc)
        {
            string manifest = PickFile("选择本项目导出时生成的 manifest.json", ".json");
            if (manifest == null) return Result.Cancel;
            Bundle bundle = ReadBundle(manifest);
            if (bundle.Schema != "DielineUV.project.v1")
                throw new InvalidDataException("这是样例对应关系。请选当前项目导出生成的 manifest.json。");
            string image = PickFile("选择与该项目画板对应的整张 PNG", ".png");
            if (image == null) return Result.Cancel;
            Canvas canvas = MatchPngCanvas(image, bundle, bundle.Canvas);
            Apply(doc, Path.GetDirectoryName(manifest), bundle, canvas, image, 0);
            RhinoApp.WriteLine("已为当前项目的 {0} 个原件更新贴图。切换到渲染显示模式查看。", bundle.Parts.Length);
            return Result.Success;
        }

        private static List<ProjectPart> FindLayoutParts(RhinoDoc doc, Canvas frame, double tolerance)
        {
            var results = new List<ProjectPart>();
            var candidates = doc.Objects.GetObjectList(new ObjectEnumeratorSettings
            {
                NormalObjects = true,
                LockedObjects = true,
                HiddenObjects = false,
                ObjectTypeFilter = PartTypes | ObjectType.Curve
            }).Where(o => o.Attributes.GetUserString(LayoutRole) == "FlatCopy" &&
                InsideFrame(o.Geometry.GetBoundingBox(true), frame, tolerance)).ToArray();
            var pairs = candidates.Select(item => new { Item = item, Source = FindSource(doc, item) }).ToArray();
            var groups = pairs.Where(pair => pair.Source != null).GroupBy(pair => pair.Source.Id);
            int ignored = pairs.Count(pair => pair.Source == null);
            if (ignored != 0)
                RhinoApp.WriteLine("有 {0} 个排版对象无法找到唯一原件，已跳过。", ignored);
            foreach (var group in groups)
            {
                var source = group.First().Source;
                var flat = group.Select(pair => pair.Item).ToArray();
                string label = PartName(source, flat[0]);
                var folded = flat.Where(o => o.Attributes.GetUserString("WoodSheetLayoutLinkMode") == FoldMode).ToArray();
                if (folded.Length != 0)
                {
                    if (folded.Length != 1)
                        throw new InvalidDataException("折弯件 " + label + " 在同一外框中有多个展开副本，请保留本次要贴图的一个。");
                    results.Add(ReadFoldPart(source, folded[0], tolerance));
                    continue;
                }
                if (flat.Any(o => o.Attributes.GetUserString("WoodSheetLayoutLinkMode") == "Developed"))
                {
                    RhinoApp.WriteLine("跳过 {0}：旧展开未保存逐面 UV，请先运行“展开折弯件并记录对应关系”。", label);
                    continue;
                }
                var solids = flat.Where(o => IsPart(o)).ToArray();
                if (solids.Length > 1)
                {
                    RhinoApp.WriteLine("跳过 {0}：同一外框内有多个排版实体，无法唯一确定位置。", label);
                    continue;
                }
                Transform toLayout;
                if (solids.Length == 1)
                {
                    if (!TryMatchVertices(source.Geometry, solids[0].Geometry, tolerance, out toLayout))
                    {
                        RhinoApp.WriteLine("跳过 {0}：原件与排版件的几何无法做刚体匹配。", label);
                        continue;
                    }
                }
                else if (!TryMatchCurveLayout(source, flat, tolerance, out toLayout))
                {
                    RhinoApp.WriteLine("跳过 {0}：二维轮廓缺少有效的原件变换，或轮廓已经重新排版。可手动绑定。", label);
                    continue;
                }
                var guides = flat.SelectMany(o => OutlineCurves(o.Geometry)).ToArray();
                if (guides.Length != 0)
                    results.Add(new ProjectPart { Source = source, Label = label, ToLayout = toLayout, Guides = guides });
            }
            return results;
        }

        private static RhinoObject FindSource(RhinoDoc doc, RhinoObject flat)
        {
            Guid sourceId;
            string savedId = flat.Attributes.GetUserString(LayoutSource) ??
                flat.Attributes.GetUserString("WoodJointPro.SourceId");
            if (Guid.TryParse(savedId, out sourceId))
            {
                var source = doc.Objects.FindId(sourceId);
                if (IsPart(source) && source.Id != flat.Id && source.Attributes.GetUserString(LayoutRole) != "FlatCopy")
                    return source;
            }
            var matches = new Dictionary<Guid, RhinoObject>();
            foreach (int group in flat.Attributes.GetGroupList() ?? new int[0])
                foreach (var member in doc.Groups.GroupMembers(group) ?? new RhinoObject[0])
                    if (IsPart(member) && member.Attributes.GetUserString(LayoutRole) == "Source")
                        matches[member.Id] = member;
            return matches.Count == 1 ? matches.Values.First() : null;
        }

        private static bool IsPart(RhinoObject obj)
        {
            return obj != null && (obj.ObjectType & PartTypes) != ObjectType.None;
        }

        private static string PartName(RhinoObject source, RhinoObject flat)
        {
            if (!string.IsNullOrWhiteSpace(source.Name)) return source.Name;
            if (!string.IsNullOrWhiteSpace(flat.Name)) return flat.Name;
            return source.Id.ToString("N").Substring(0, 8);
        }

        private static bool InsideFrame(BoundingBox bounds, Canvas frame, double tolerance)
        {
            return bounds.IsValid && bounds.Min.X >= frame.WorldMin[0] - tolerance &&
                bounds.Min.Y >= frame.WorldMin[1] - tolerance &&
                bounds.Max.X <= frame.WorldMax[0] + tolerance && bounds.Max.Y <= frame.WorldMax[1] + tolerance;
        }

        private static IEnumerable<Curve> OutlineCurves(GeometryBase geometry)
        {
            var curve = geometry as Curve;
            if (curve != null) return new[] { curve };
            var mesh = geometry as Mesh;
            if (mesh != null)
                return (mesh.GetNakedEdges() ?? new Polyline[0]).Select(line => (Curve)new PolylineCurve(line));
            var brep = Brep.TryConvertBrep(geometry);
            return brep == null ? new Curve[0] : brep.Edges.Select(edge => edge.DuplicateCurve()).ToArray();
        }

        private static Point3d[] Vertices(GeometryBase geometry)
        {
            var mesh = geometry as Mesh;
            if (mesh != null) return mesh.Vertices.Select(p => new Point3d(p.X, p.Y, p.Z)).ToArray();
            var brep = Brep.TryConvertBrep(geometry);
            return brep == null ? new Point3d[0] : brep.Vertices.Select(v => v.Location).ToArray();
        }

        private static bool TryMatchVertices(GeometryBase source, GeometryBase flat, double tolerance, out Transform transform)
        {
            transform = Transform.Unset;
            var a = Vertices(source);
            var b = Vertices(flat);
            if (a.Length < 3 || a.Length != b.Length) return false;
            int far = 0, side = 0;
            for (int i = 1; i < a.Length; i++)
                if (a[i].DistanceToSquared(a[0]) > a[far].DistanceToSquared(a[0])) far = i;
            var direction = a[far] - a[0];
            double distance = 0;
            for (int i = 1; i < a.Length; i++)
            {
                double area = Vector3d.CrossProduct(direction, a[i] - a[0]).SquareLength;
                if (area > distance) { distance = area; side = i; }
            }
            if (!TryRegister(a[0], a[far], a[side], b[0], b[far], b[side], tolerance, out transform))
                return false;
            for (int i = 0; i < a.Length; i++)
                if ((transform * a[i]).DistanceTo(b[i]) > tolerance) return false;
            return true;
        }

        private static bool TryRegister(Point3d a0, Point3d a1, Point3d a2,
            Point3d b0, Point3d b1, Point3d b2, double tolerance, out Transform transform)
        {
            transform = Transform.Unset;
            var from = new Plane(a0, a1, a2);
            var to = new Plane(b0, b1, b2);
            if (!from.IsValid || !to.IsValid) return false;
            transform = Transform.PlaneToPlane(from, to);
            return (transform * a1).DistanceTo(b1) <= tolerance &&
                (transform * a2).DistanceTo(b2) <= tolerance;
        }

        private static bool TryMatchCurveLayout(RhinoObject source, RhinoObject[] outlines, double tolerance, out Transform transform)
        {
            transform = Transform.Unset;
            string data = outlines[0].Attributes.GetUserString(LayoutTransform) ??
                outlines[0].Attributes.GetUserString("WoodJointPro.SourceToFlatTransform");
            if (string.IsNullOrWhiteSpace(data)) return false;
            var cells = data.Split(';');
            if (cells.Length != 16) return false;
            var saved = Transform.Identity;
            for (int i = 0; i < cells.Length; i++)
            {
                double value;
                if (!double.TryParse(cells[i], NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                    double.IsNaN(value) || double.IsInfinity(value)) return false;
                saved[i / 4, i % 4] = value;
            }
            if (Math.Abs(saved.M30) > 1e-8 || Math.Abs(saved.M31) > 1e-8 ||
                Math.Abs(saved.M32) > 1e-8 || Math.Abs(saved.M33 - 1) > 1e-8) return false;
            var expected = source.Geometry.GetBoundingBox(saved);
            var actual = BoundingBox.Empty;
            foreach (var outline in outlines) actual.Union(outline.Geometry.GetBoundingBox(true));
            if (!expected.IsValid || !actual.IsValid ||
                Math.Abs((expected.Max.X - expected.Min.X) - (actual.Max.X - actual.Min.X)) > tolerance ||
                Math.Abs((expected.Max.Y - expected.Min.Y) - (actual.Max.Y - actual.Min.Y)) > tolerance)
                return false;
            transform = Transform.Translation(actual.Min.X - expected.Min.X, actual.Min.Y - expected.Min.Y, 0) * saved;
            return ProjectedCurvesFit(source.Geometry, transform, outlines, tolerance);
        }

        private static bool ProjectedCurvesFit(GeometryBase source, Transform transform, RhinoObject[] outlines, double tolerance)
        {
            var projected = new List<Curve>();
            try
            {
                var xy = Transform.PlanarProjection(Plane.WorldXY);
                foreach (var edge in OutlineCurves(source))
                {
                    var curve = edge.DuplicateCurve();
                    if (curve.Transform(xy * transform) && curve.GetLength() > tolerance)
                        projected.Add(curve);
                    else
                        curve.Dispose();
                }
                if (projected.Count == 0) return false;
                foreach (var obj in outlines)
                {
                    var outline = obj.Geometry as Curve;
                    if (outline == null) return false;
                    for (int i = 0; i <= 4; i++)
                    {
                        var p = outline.PointAt(outline.Domain.ParameterAt(i / 4.0));
                        p.Z = 0;
                        if (!projected.Any(curve => {
                            double parameter;
                            return curve.ClosestPoint(p, out parameter, tolerance);
                        })) return false;
                    }
                }
                return true;
            }
            finally { foreach (var curve in projected) curve.Dispose(); }
        }

        private static List<ProjectPart> BindParts(RhinoDoc doc, Canvas frame, double tolerance)
        {
            var parts = new List<ProjectPart>();
            while (true)
            {
                ObjRef sourceRef;
                if (RhinoGet.GetOneObject("选择要绑定的原三维平板，回车结束绑定", false, PartTypes, out sourceRef) != Result.Success)
                    return parts;
                using (sourceRef)
                {
                    var source = sourceRef.Object();
                    if (source == null || parts.Any(part => part.Source.Id == source.Id))
                        throw new InvalidOperationException("这个原件已绑定，或无法读取。");
                    ObjRef[] layoutRefs;
                    if (RhinoGet.GetMultipleObjects("选择这一个零件的排版轮廓或铺平实体，回车确认",
                        false, PartTypes | ObjectType.Curve, out layoutRefs) != Result.Success) return parts;
                    var guides = new List<Curve>();
                    try
                    {
                        foreach (var reference in layoutRefs)
                        {
                            var geometry = reference.Geometry();
                            if (geometry == null || reference.ObjectId == source.Id ||
                                !InsideFrame(geometry.GetBoundingBox(true), frame, tolerance))
                                throw new InvalidDataException("排版对象必须完整位于选定外框内。");
                            guides.AddRange(OutlineCurves(geometry));
                        }
                    }
                    finally { foreach (var reference in layoutRefs) reference.Dispose(); }

                    var a = new Point3d[3];
                    var b = new Point3d[3];
                    for (int i = 0; i < 3; i++)
                    {
                        if (RhinoGet.GetPoint("捕捉原件大面上的定位点 " + (i + 1), false, out a[i]) != Result.Success ||
                            RhinoGet.GetPoint("捕捉排版轮廓上对应点 " + (i + 1), false, out b[i]) != Result.Success)
                            return parts;
                    }
                    Transform registration;
                    if (Math.Abs(b[0].Z - b[1].Z) > tolerance || Math.Abs(b[0].Z - b[2].Z) > tolerance ||
                        !TryRegister(a[0], a[1], a[2], b[0], b[1], b[2], tolerance, out registration))
                        throw new InvalidDataException("三对点需要不共线、等尺寸，排版点需要位于同一水平面。");
                    if (!InsideFrame(source.Geometry.GetBoundingBox(registration), frame, tolerance))
                        throw new InvalidDataException("定位后原件超出外框，请检查三对点是否一一对应。");
                    if (guides.Count == 0) throw new InvalidDataException("找不到可绘制的排版轮廓。");
                    parts.Add(new ProjectPart { Source = source, Label = PartName(source, source),
                        Guides = guides.ToArray(), ToLayout = registration });
                }
            }
        }

        private static void WriteProject(List<ProjectPart> parts, Canvas frame, string output)
        {
            var entries = new List<Part>();
            using (var file = new File3dm())
            {
                file.Settings.ModelUnitSystem = UnitSystem.Millimeters;
                foreach (var part in parts)
                {
                    var source = part.Source;
                    var original = new ObjectAttributes { ObjectId = source.Id, Name = part.Label };
                    Guid saved;
                    if (source.Geometry is Extrusion) saved = file.Objects.AddExtrusion((Extrusion)source.Geometry, original);
                    else if (source.Geometry is Mesh) saved = file.Objects.AddMesh((Mesh)source.Geometry, original);
                    else if (source.Geometry is Brep) saved = file.Objects.AddBrep((Brep)source.Geometry, original);
                    else saved = file.Objects.AddSurface((Surface)source.Geometry, original);
                    if (saved != source.Id) throw new InvalidOperationException("无法保存原件编号：" + part.Label);
                    using (var mesh = part.MappingMesh == null ? BuildProjectMesh(source.Geometry) : part.MappingMesh.DuplicateMesh())
                    {
                        var min = new[] { double.PositiveInfinity, double.PositiveInfinity };
                        var max = new[] { double.NegativeInfinity, double.NegativeInfinity };
                        for (int i = 0; i < mesh.Vertices.Count; i++)
                        {
                            var v = mesh.Vertices[i];
                            var p = part.LayoutPoints == null ? part.ToLayout * new Point3d(v.X, v.Y, v.Z) : part.LayoutPoints[i];
                            double u = (p.X - frame.WorldMin[0]) / (frame.WorldMax[0] - frame.WorldMin[0]);
                            double w = (p.Y - frame.WorldMin[1]) / (frame.WorldMax[1] - frame.WorldMin[1]);
                            if (!mesh.TextureCoordinates.SetTextureCoordinate(i, u, w))
                                throw new InvalidOperationException("无法保存 UV 坐标：" + part.Label);
                            min[0] = Math.Min(min[0], u); min[1] = Math.Min(min[1], w);
                            max[0] = Math.Max(max[0], u); max[1] = Math.Max(max[1], w);
                        }
                        Guid mapId = file.Objects.AddMesh(mesh, new ObjectAttributes { Name = "UV " + part.Label });
                        if (mapId == Guid.Empty) throw new InvalidOperationException("无法保存贴图网格：" + part.Label);
                        entries.Add(new Part { Label = part.Label, SourceId = source.Id.ToString(),
                            MappingId = mapId.ToString(), UvMin = min, UvMax = max });
                    }
                }
                var bundle = new Bundle { Schema = "DielineUV.project.v1", Canvas = frame, Parts = entries.ToArray() };
                ValidateFrameCoversParts(bundle, frame);
                if (!file.Write(Path.Combine(output, "mapping.3dm"), 7))
                    throw new IOException("无法写出项目贴图映射。");
                string guides = Path.Combine(output, "template-crop.svg");
                WriteProjectGuides(parts, frame, guides);
                WriteFrameTemplates(guides, output, frame, frame);
                WriteProjectCalibration(frame, Path.Combine(output, "calibration.png"));
                using (var json = File.Create(Path.Combine(output, "manifest.json")))
                    new DataContractJsonSerializer(typeof(Bundle)).WriteObject(json, bundle);
            }
        }

        private static Mesh BuildProjectMesh(GeometryBase geometry)
        {
            var original = geometry as Mesh;
            if (original != null) return original.DuplicateMesh();
            var brep = Brep.TryConvertBrep(geometry);
            var fragments = brep == null ? null : Mesh.CreateFromBrep(brep, MeshingParameters.QualityRenderMesh);
            if (fragments == null || fragments.Length == 0) throw new InvalidOperationException("原件无法生成渲染网格。");
            var mesh = new Mesh();
            foreach (var fragment in fragments) mesh.Append(fragment);
            return mesh;
        }

        private static void WriteProjectCalibration(Canvas frame, string path)
        {
            int cell = Math.Max(32, (int)Math.Ceiling(Math.Max(frame.Width, frame.Height) / 20.0));
            using (var bitmap = new Eto.Drawing.Bitmap(frame.Width, frame.Height, Eto.Drawing.PixelFormat.Format32bppRgb))
            {
                using (var graphics = new Eto.Drawing.Graphics(bitmap))
                using (var font = Eto.Drawing.SystemFonts.Default(Math.Max(10, cell / 8f)))
                {
                    graphics.Clear(Eto.Drawing.Colors.White);
                    int columns = (frame.Width + cell - 1) / cell;
                    int rows = (frame.Height + cell - 1) / cell;
                    for (int row = 0; row < rows; row++)
                        for (int col = 0; col < columns; col++)
                        {
                            int red = 80 + 140 * col / Math.Max(1, columns - 1);
                            int blue = 80 + 140 * row / Math.Max(1, rows - 1);
                            var color = ((row + col) & 1) == 0 ? Eto.Drawing.Color.FromArgb(red, 210, blue, 255) : Eto.Drawing.Colors.WhiteSmoke;
                            var rectangle = new Eto.Drawing.RectangleF(col * cell, row * cell, cell, cell);
                            graphics.FillRectangle(color, rectangle);
                            graphics.DrawRectangle(Eto.Drawing.Colors.DimGray, rectangle);
                            graphics.DrawText(font, Eto.Drawing.Colors.Black,
                                new Eto.Drawing.PointF(col * cell + 4, row * cell + 4), (col + 1) + "/" + (row + 1));
                        }
                }
                using (var png = new MemoryStream())
                {
                    bitmap.Save(png, Eto.Drawing.ImageFormat.Png);
                    SavePngWithPhysicalSize(png.ToArray(), path, frame);
                }
            }
        }

        private static void WriteProjectGuides(IEnumerable<ProjectPart> parts, Canvas frame, string path)
        {
            const string ns = "http://www.w3.org/2000/svg";
            var settings = new XmlWriterSettings { Indent = true };
            using (var writer = XmlWriter.Create(path, settings))
            {
                writer.WriteStartElement("svg", ns);
                writer.WriteStartElement("g", ns);
                writer.WriteAttributeString("id", "REFERENCE_OUTLINES");
                writer.WriteAttributeString("fill", "none");
                writer.WriteAttributeString("stroke", "#555");
                writer.WriteAttributeString("stroke-width", "0.1");
                var projection = Transform.PlanarProjection(Plane.WorldXY);
                foreach (var part in parts)
                    foreach (var guide in part.Guides)
                        using (var xy = guide.DuplicateCurve())
                        {
                            if (!xy.Transform(projection) || xy.GetLength() <= 0.001) continue;
                            Polyline points;
                            if (!xy.TryGetPolyline(out points))
                            {
                                using (var approximation = xy.ToPolyline(0.03, 0.08, 0.001, 3))
                                    if (approximation == null || !approximation.TryGetPolyline(out points)) continue;
                            }
                            writer.WriteStartElement("polyline", ns);
                            writer.WriteAttributeString("data-part", part.Label);
                            writer.WriteAttributeString("points", string.Join(" ", points.Select(p =>
                                string.Format(CultureInfo.InvariantCulture, "{0:G17},{1:G17}",
                                    p.X - frame.WorldMin[0], frame.WorldMax[1] - p.Y))));
                            writer.WriteEndElement();
                        }
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
        }
    }
}
