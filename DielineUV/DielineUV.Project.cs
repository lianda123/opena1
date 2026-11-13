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
using Rhino.Input.Custom;
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
            public int[] PrintFaces;
        }

        private static Result ExportProject(RhinoDoc doc)
        {
            var frame = SelectFrame(doc);
            if (frame == null) return Result.Cancel;
            var options = new[] { "识别排版关联", "选择原件和刀模（自动对齐）", "手动绑定平板（三对定位点）" };
            var option = Dialogs.ShowListBox(Title, "选择当前外框内零件的匹配方式", options) as string;
            if (option == null) return Result.Cancel;
            double tolerance = Math.Max(doc.ModelAbsoluteTolerance * 4.0, 0.02);
            var parts = option == options[0] ? FindLayoutParts(doc, frame, tolerance) :
                BindParts(doc, frame, tolerance, option == options[1]);
            if (parts == null) return Result.Cancel;
            if (parts.Count == 0)
            {
                if (option != options[0]) return Result.Cancel;
                throw new InvalidOperationException("本外框内没有可用关联。请选排版件所在的外框，或使用“选择原件和刀模（自动对齐）”。");
            }
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
            finally
            {
                foreach (var part in parts)
                {
                    part.MappingMesh?.Dispose();
                    if (option != options[0])
                        foreach (var guide in part.Guides) guide.Dispose();
                }
            }
        }

        private static Result ImportProject(RhinoDoc doc)
        {
            string image = PickFile("选择你做好的整张 PNG 贴图", ".png");
            if (image == null) return Result.Cancel;
            string imageFolder = Path.GetDirectoryName(image);
            string nearbyManifest = Path.Combine(imageFolder, "manifest.json");
            string manifest = null;
            if (File.Exists(nearbyManifest) && File.Exists(Path.Combine(imageFolder, "mapping.3dm")))
            {
                var options = new[] { "使用 PNG 同目录的项目模板", "选择另一份 manifest.json" };
                var choice = Dialogs.ShowListBox(Title, "这张 PNG 对应哪份项目模板？", options) as string;
                if (choice == null) return Result.Cancel;
                if (choice == options[0]) manifest = nearbyManifest;
            }
            if (manifest == null)
                manifest = PickFile("选择这张 PNG 对应的项目 manifest.json", ".json");
            if (manifest == null) return Result.Cancel;

            Bundle bundle = ReadBundle(manifest);
            ValidatePngCanvas(image, bundle.Canvas);
            var finish = ChooseSideFinish();
            if (finish == null || !Apply(doc, Path.GetDirectoryName(manifest), bundle, image, finish))
                return Result.Cancel;
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

        private static List<ProjectPart> BindParts(RhinoDoc doc, Canvas frame, double tolerance, bool automatic)
        {
            var parts = new List<ProjectPart>();
            bool finished = false;
            try
            {
                while (true)
                {
                    RhinoObject source;
                    string mode = automatic ? "自动对齐" : "手动绑定";
                    using (var picker = CreateObjectPicker(
                        string.Format("{0}｜已绑定 {1} 件。点选三维原件（可在外框外），或回车导出", mode, parts.Count),
                        PartTypes | ObjectType.PolysrfFilter))
                    {
                        int auto = picker.AddOption(new LocalizeStringPair("Automatic", "自动对齐"));
                        picker.AddOption(new LocalizeStringPair("Manual", "手动绑定"));
                        picker.AcceptNothing(true);
                        var result = picker.Get();
                        if (result == GetResult.Nothing)
                        {
                            finished = true;
                            return parts;
                        }
                        if (result == GetResult.Option)
                        {
                            automatic = picker.Option().Index == auto;
                            continue;
                        }
                        if (result != GetResult.Object) return null;
                        using (var reference = picker.Object(0)) source = reference.Object();
                    }
                    if (source == null || !IsPart(source) || source.IsLocked || source.IsReference ||
                        source.Attributes.GetUserString(LayoutRole) == "FlatCopy" ||
                        parts.Any(part => part.Source.Id == source.Id))
                    {
                        RhinoApp.WriteLine("请选择尚未绑定、可以编辑的三维原件。");
                        continue;
                    }

                    var partToAdd = BindPart(doc, source, frame, tolerance, automatic);
                    if (partToAdd == null) continue;
                    parts.Add(partToAdd);
                    RhinoApp.WriteLine("已绑定 {0}。下一件可选择自动对齐或手动绑定。", partToAdd.Label);
                }
            }
            finally
            {
                if (!finished)
                    foreach (var part in parts)
                        foreach (var guide in part.Guides) guide.Dispose();
            }
        }

        private static ProjectPart BindPart(RhinoDoc doc, RhinoObject source, Canvas frame, double tolerance, bool automatic)
        {
            var layout = PickBindingLayout(source.Id, frame, tolerance);
            if (layout == null) return null;
            string label = PartName(source, source);
            if (automatic)
            {
                var guides = BindingGuides(layout, tolerance, true);
                bool keepGuides = false;
                try
                {
                    Point3d[] markers;
                    var alignments = FindBoardAlignments(source.Geometry, guides, tolerance, out markers);
                    alignments.RemoveAll(transform => !InsideFrame(source.Geometry.GetBoundingBox(transform), frame, tolerance));
                    if (alignments.Count != 0)
                    {
                        Transform registration;
                        if (!ChooseBoardAlignment(doc, alignments, markers, guides[0].GetBoundingBox(true).Center.Z, out registration))
                            return null;
                        keepGuides = true;
                        return new ProjectPart { Source = source, Label = label, Guides = guides, ToLayout = registration };
                    }
                }
                finally { if (!keepGuides) foreach (var guide in guides) guide.Dispose(); }
                RhinoApp.WriteLine("零件 {0} 无法自动对齐，已转为手动绑定。请在已选原件和刀模上各点三个对应点。", label);
            }

            var manualGuides = BindingGuides(layout, tolerance, false);
            if (manualGuides.Length == 0)
            {
                RhinoApp.WriteLine("找不到可用于定位的刀模边线。请重选这个零件的轮廓。");
                return null;
            }
            bool keepManualGuides = false;
            try
            {
                while (true)
                {
                    Transform registration;
                    var result = PickManualRegistration(source.Geometry, manualGuides, tolerance, out registration);
                    if (result == Result.Cancel)
                    {
                        RhinoApp.WriteLine("已跳过 {0}。可以重新选择这个零件，或继续绑定其他零件。", label);
                        return null;
                    }
                    if (result != Result.Success) continue;
                    if (!InsideFrame(source.Geometry.GetBoundingBox(registration), frame, tolerance))
                    {
                        RhinoApp.WriteLine("贴图范围超出外框。请重新点对应位置，或按 Esc 返回原件选择。");
                        continue;
                    }
                    keepManualGuides = true;
                    return new ProjectPart { Source = source, Label = label, Guides = manualGuides, ToLayout = registration };
                }
            }
            finally { if (!keepManualGuides) foreach (var guide in manualGuides) guide.Dispose(); }
        }

        private static Result PickManualRegistration(GeometryBase geometry, Curve[] guides, double tolerance, out Transform registration)
        {
            registration = Transform.Unset;
            var sourceSnaps = Vertices(geometry);
            var mesh = geometry as Mesh;
            var brep = geometry as Brep;
            var converted = mesh == null && brep == null ? Brep.TryConvertBrep(geometry) : null;
            try
            {
                brep = brep ?? converted;
                if (mesh == null && brep == null) throw new InvalidDataException("无法读取原件的表面。");
                var original = PickBindingPoints("原件", sourceSnaps, point =>
                {
                    if (mesh != null) return mesh.ClosestMeshPoint(point, tolerance) != null;
                    var closest = brep.ClosestPoint(point);
                    return closest.IsValid && closest.DistanceTo(point) <= tolerance;
                }, tolerance);
                if (original == null) return Result.Cancel;
                var flat = PickBindingPoints("刀模", guides.SelectMany(BindingCurveCorners).Distinct().ToArray(),
                    point => guides.Any(curve => {
                        double parameter;
                        return curve.ClosestPoint(point, out parameter, tolerance);
                    }), tolerance);
                if (flat == null) return Result.Cancel;
                if (Math.Abs(flat[0].Z - flat[1].Z) <= tolerance && Math.Abs(flat[0].Z - flat[2].Z) <= tolerance &&
                    TryRegister(original[0], original[1], original[2], flat[0], flat[1], flat[2], tolerance, out registration))
                    return Result.Success;
                RhinoApp.WriteLine("三对点的顺序或间距不一致，或者刀模点不在同一高度。请重新点对应位置。");
                return Result.Failure;
            }
            finally { converted?.Dispose(); }
        }

        private static GetObject CreateObjectPicker(string prompt, ObjectType geometryFilter)
        {
            var picker = new GetObject
            {
                GeometryFilter = geometryFilter,
                GroupSelect = false,
                SubObjectSelect = false,
                DeselectAllBeforePostSelect = true
            };
            picker.EnablePreSelect(false, true);
            picker.SetCommandPrompt(prompt);
            return picker;
        }

        private static GeometryBase[] PickBindingLayout(Guid sourceId, Canvas frame, double tolerance)
        {
            while (true)
            {
                using (var picker = CreateObjectPicker("点选或框选这一个零件的刀模轮廓或铺平实体，回车确认", PartTypes | ObjectType.PolysrfFilter | ObjectType.Curve))
                {
                    picker.SetCustomGeometryFilter((obj, geometry, component) => obj.Id != sourceId && obj.Id != frame.FrameObjectId);
                    if (picker.GetMultiple(1, 0) != GetResult.Object) return null;
                    var layout = new List<GeometryBase>();
                    for (int i = 0; i < picker.ObjectCount; i++)
                    {
                        using (var reference = picker.Object(i))
                        {
                            var obj = reference.Object();
                            var geometry = obj?.Geometry;
                            if (geometry == null || !InsideFrame(geometry.GetBoundingBox(true), frame, tolerance))
                            {
                                string label = string.IsNullOrWhiteSpace(obj?.Name)
                                    ? reference.ObjectId.ToString("N").Substring(0, 8)
                                    : obj.Name;
                                RhinoApp.WriteLine("刀模选区里的对象「{0}」超出外框或无法读取。请重选框内的刀模轮廓。", label);
                                break;
                            }
                            layout.Add(geometry);
                        }
                    }
                    if (layout.Count == picker.ObjectCount) return layout.ToArray();
                }
            }
        }

        private static Curve[] BindingGuides(IEnumerable<GeometryBase> layout, double tolerance, bool automatic)
        {
            var guides = new List<Curve>();
            foreach (var geometry in layout)
            {
                if (geometry is Curve curve) guides.Add(curve.DuplicateCurve());
                else if (!automatic) guides.AddRange(OutlineCurves(geometry));
                else
                {
                    using (var board = ReadBoardOutline(geometry, tolerance))
                    {
                        if (board == null) continue;
                        guides.Add(board.Outer.DuplicateCurve());
                        guides.AddRange(board.Holes.Select(hole => hole.DuplicateCurve()));
                    }
                }
            }
            return guides.ToArray();
        }

        private static IEnumerable<Point3d> BindingCurveCorners(Curve curve)
        {
            Polyline polyline;
            if (curve.TryGetPolyline(out polyline)) return polyline;
            var segments = curve.DuplicateSegments();
            if (segments == null || segments.Length == 0) return new[] { curve.PointAtStart, curve.PointAtEnd };
            try { return segments.SelectMany(segment => new[] { segment.PointAtStart, segment.PointAtEnd }).ToArray(); }
            finally { foreach (var segment in segments) segment.Dispose(); }
        }

        private static Point3d[] PickBindingPoints(string side, Point3d[] corners, Func<Point3d, bool> onGeometry, double tolerance)
        {
            var picked = new List<Point3d>(3);
            var snaps = corners.Where(point => point.IsValid).Distinct().Take(4096).ToArray();
            while (picked.Count < 3)
            {
                using (var picker = new GetPoint())
                {
                    picker.SetCommandPrompt(side == "原件"
                        ? "原件 " + (picked.Count + 1) + "/3：点同一大面上的角点"
                        : "刀模 " + (picked.Count + 1) + "/3：按原件顺序点对应角点");
                    picker.PermitOrthoSnap(false);
                    if (snaps.Length != 0) picker.AddSnapPoints(snaps);
                    picker.DynamicDraw += (sender, args) =>
                    {
                        for (int i = 0; i < picked.Count; i++)
                            args.Display.DrawDot(picked[i], (i + 1).ToString(CultureInfo.InvariantCulture));
                    };
                    if (picker.Get() != GetResult.Point) return null;
                    var point = picker.Point();
                    Point3d corner;
                    if (point.IsValid && !snaps.Any(candidate => candidate.DistanceTo(point) <= tolerance) &&
                        TrySnapBindingCorner(picker, snaps, tolerance, out corner))
                        point = corner;
                    if (!point.IsValid || !onGeometry(point))
                    {
                        RhinoApp.WriteLine("定位点需要落在已选的{0}上。请靠近角点点击，或打开端点捕捉。", side);
                        continue;
                    }
                    if (picked.Any(previous => previous.DistanceTo(point) <= tolerance) ||
                        (picked.Count == 2 && new Line(picked[0], picked[1]).DistanceTo(point, false) <= tolerance))
                    {
                        RhinoApp.WriteLine("请点一个不同且不共线的角点。");
                        continue;
                    }
                    picked.Add(point);
                }
            }
            return picked.ToArray();
        }

        private static bool TrySnapBindingCorner(GetPoint picker, Point3d[] corners, double tolerance, out Point3d point)
        {
            point = Point3d.Unset;
            var view = picker.View();
            var click = picker.Point2d();
            if (view == null || (click.X == 0 && click.Y == 0)) return false;
            var viewport = view.ActiveViewport;
            var nearby = new List<KeyValuePair<double, Point3d>>();
            foreach (var corner in corners)
            {
                var screen = viewport.WorldToClient(corner);
                var dx = screen.X - click.X;
                var dy = screen.Y - click.Y;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance <= 12) nearby.Add(new KeyValuePair<double, Point3d>(distance, corner));
            }
            if (nearby.Count == 0) return false;
            var closest = nearby.OrderBy(candidate => candidate.Key).First();
            if (nearby.Any(candidate => candidate.Key <= closest.Key + 1 &&
                candidate.Value.DistanceTo(closest.Value) > tolerance)) return false;
            point = closest.Value;
            return true;
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
                            MappingId = mapId.ToString(), UvMin = min, UvMax = max,
                            PrintFaces = part.MappingMesh == null ? FlatPrintFaces(source.Geometry, part.ToLayout) : part.PrintFaces });
                    }
                }
                var bundle = new Bundle { Schema = ProjectSchema, Canvas = frame, Parts = entries.ToArray() };
                ValidateFrameCoversParts(bundle);
                if (!file.Write(Path.Combine(output, "mapping.3dm"), 7))
                    throw new IOException("无法写出项目贴图映射。");
                string guides = Path.Combine(output, "template.svg");
                WriteProjectGuides(parts, frame, guides);
                WritePngTemplate(guides, output, frame);
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
                double width = frame.WorldMax[0] - frame.WorldMin[0];
                double height = frame.WorldMax[1] - frame.WorldMin[1];
                var culture = CultureInfo.InvariantCulture;
                writer.WriteAttributeString("width", width.ToString("G17", culture) + "mm");
                writer.WriteAttributeString("height", height.ToString("G17", culture) + "mm");
                writer.WriteAttributeString("viewBox", string.Format(culture, "0 0 {0:G17} {1:G17}", width, height));
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
