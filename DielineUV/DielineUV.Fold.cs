using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
        private const string FoldMode = "DielineUVUnrolled";
        private const string FoldArchiveKey = "DielineUV.FoldArchive.v1";

        private sealed class FoldGeometry : IDisposable
        {
            public Brep Flat;
            public Mesh OriginalMesh;
            public Mesh FlatMesh;
            public void Dispose()
            {
                if (Flat != null) Flat.Dispose();
                if (OriginalMesh != null) OriginalMesh.Dispose();
                if (FlatMesh != null) FlatMesh.Dispose();
            }
        }

        private static Result PrepareFold(RhinoDoc doc)
        {
            if (doc.ModelUnitSystem != UnitSystem.Millimeters)
                throw new InvalidOperationException("请在毫米单位模型中展开折弯件。");
            ObjRef sourceRef;
            if (RhinoGet.GetOneObject("选择原三维折弯件（曲面或多重曲面）", false,
                ObjectType.Brep | ObjectType.Surface | ObjectType.Extrusion, out sourceRef) != Result.Success)
                return Result.Cancel;
            using (sourceRef)
            {
                var source = sourceRef.Object();
                if (source == null || source.IsLocked || source.IsReference)
                    throw new InvalidOperationException("请先解锁原件；参照模型中的对象需复制到当前模型。");
                if (source.Attributes.GetUserString(LayoutRole) == "FlatCopy")
                    throw new InvalidDataException("选到的是排版件，请选择原三维折弯件。");
                using (var brep = source.Geometry is Brep ? ((Brep)source.Geometry).DuplicateBrep() : Brep.TryConvertBrep(source.Geometry))
                {
                    if (brep == null || !brep.IsValid)
                        throw new InvalidDataException("无法读取折弯件曲面。请先修复无效几何。");
                    int[] indices;
                    if (brep.IsSolid)
                    {
                        RhinoApp.WriteLine("带厚度实体：请选出要印图的一侧相连表面，包含直面和弯曲面，不选背面与厚度侧面。");
                        indices = SelectFoldFaces(source);
                    }
                    else
                    {
                        var modes = new[] { "展开整个开放曲面", "只选择要印图的表面" };
                        var mode = Dialogs.ShowListBox(Title, "选择折弯件的贴图表面", modes) as string;
                        if (mode == null) return Result.Cancel;
                        indices = mode == modes[0] ? Enumerable.Range(0, brep.Faces.Count).ToArray() : SelectFoldFaces(source);
                    }
                    if (indices == null) return Result.Cancel;
                    using (var skin = brep.DuplicateSubBrep(indices))
                    using (var fold = UnrollFold(skin, doc.ModelAbsoluteTolerance))
                    {
                        Point3d location;
                        if (RhinoGet.GetPoint("指定展开件左下角的排版位置（世界 XY 平面）", false, out location) != Result.Success)
                            return Result.Cancel;
                        var bounds = fold.Flat.GetBoundingBox(true);
                        var move = Transform.Translation(location.X - bounds.Min.X, location.Y - bounds.Min.Y, -bounds.Min.Z);
                        fold.Flat.Transform(move);
                        fold.FlatMesh.Transform(move);
                        string label = PartName(source, source);
                        var attributes = new ObjectAttributes { Name = label + "_贴图展开", LayerIndex = doc.Layers.CurrentLayerIndex };
                        attributes.SetUserString(LayoutRole, "FlatCopy");
                        attributes.SetUserString(LayoutSource, source.Id.ToString());
                        attributes.SetUserString("WoodSheetLayoutLinkMode", FoldMode);
                        attributes.SetUserString(FoldArchiveKey, EncodeFold(source.Geometry, fold));
                        Guid id = doc.Objects.AddBrep(fold.Flat, attributes);
                        if (id == Guid.Empty) throw new IOException("无法把展开件添加到模型。");
                        doc.Objects.UnselectAll();
                        doc.Objects.Select(id);
                        doc.Views.Redraw();
                        RhinoApp.WriteLine("已记录 {0} 的折弯贴图关系。可移动、旋转或镜像这个展开件；请勿缩放、炸开或重建它。", label);
                        RhinoApp.WriteLine("把展开件排入实际外框后，运行“导出当前项目模板”并选择“识别排版关联”。");
                        RhinoApp.WriteLine("本次按所选印图表面展开；与按中性层生成的旧刀模可能尺寸不同，请以本次模板绘图。");
                        return Result.Success;
                    }
                }
            }
        }

        private static int[] SelectFoldFaces(RhinoObject source)
        {
            using (var picker = new GetObject())
            {
                picker.SetCommandPrompt("选择该折弯件要印图的一侧相连表面（Ctrl+Shift 点选；Mac 用 Command+Shift），回车确认");
                picker.GeometryFilter = ObjectType.Surface;
                picker.SubObjectSelect = true;
                picker.GroupSelect = false;
                picker.EnablePreSelect(false, true);
                picker.GetMultiple(1, 0);
                if (picker.CommandResult() != Result.Success) return null;
                var indices = new HashSet<int>();
                for (int i = 0; i < picker.ObjectCount; i++)
                    using (var reference = picker.Object(i))
                    {
                        var face = reference.Face();
                        if (reference.ObjectId != source.Id || face == null)
                            throw new InvalidDataException("所有贴图面必须属于刚才选择的同一个三维原件。");
                        indices.Add(face.FaceIndex);
                    }
                return indices.OrderBy(i => i).ToArray();
            }
        }

        private static FoldGeometry UnrollFold(Brep skin, double documentTolerance)
        {
            double tolerance = Math.Max(documentTolerance, 0.001);
            if (skin == null || !skin.IsValid || skin.IsSolid || !ConnectedSkin(skin))
                throw new InvalidDataException("请选择一侧连续的开放表面，直面和折弯面须沿公共边连接。");
            foreach (var face in skin.Faces)
                if (face.IsClosed(0) || face.IsClosed(1))
                    throw new InvalidDataException("闭合筒面需要先沿接缝切开，再生成折弯贴图展开。");

            var result = new FoldGeometry();
            try
            {
                var parameters = MeshingParameters.QualityRenderMesh;
                parameters.RefineAngle = Math.PI / 90.0;
                parameters.MaximumEdgeLength = Math.Max(skin.GetBoundingBox(true).Diagonal.Length / 24.0, tolerance * 10);
                var fragments = Mesh.CreateFromBrep(skin, parameters);
                if (fragments == null || fragments.Length != skin.Faces.Count)
                    throw new InvalidDataException("无法为全部贴图面生成网格。");
                result.OriginalMesh = new Mesh();
                var following = new List<Point3d>();
                for (int f = 0; f < fragments.Length; f++)
                    using (var fragment = fragments[f])
                    {
                        if (fragment == null || fragment.Vertices.Count == 0)
                            throw new InvalidDataException("有贴图面无法生成有效网格。");
                        for (int i = 0; i < fragment.Vertices.Count; i++)
                        {
                            var vertex = fragment.Vertices[i];
                            double u, v;
                            var point = new Point3d(vertex.X, vertex.Y, vertex.Z);
                            if (!skin.Faces[f].ClosestPoint(point, out u, out v))
                                throw new InvalidDataException("网格顶点无法定位到原曲面。");
                            var exact = skin.Faces[f].PointAt(u, v);
                            fragment.Vertices.SetVertex(i, exact);
                            following.Add(exact);
                        }
                        result.OriginalMesh.Append(fragment);
                    }
                if (following.Count == 0 || following.Count > 60000)
                    throw new InvalidDataException("贴图表面网格过密或为空。请把复杂零件拆成连续印图区域后重试。");

                var unroller = new Unroller(skin) { AbsoluteTolerance = tolerance, RelativeTolerance = 0.001, ExplodeOutput = false };
                var dots = new List<TextDot>();
                try
                {
                    for (int i = 0; i < following.Count; i++)
                    {
                        var dot = new TextDot(i.ToString(CultureInfo.InvariantCulture), following[i]);
                        dots.Add(dot);
                        unroller.AddFollowingGeometry(dot);
                    }
                    Curve[] curves;
                    Point3d[] points;
                    TextDot[] flatDots;
                    var flatBreps = unroller.PerformUnroll(out curves, out points, out flatDots);
                    try
                    {
                        if (flatBreps == null || flatBreps.Length == 0)
                            throw new InvalidDataException("该表面不能精确展开。球面等双曲率表面需要专门的变形展开流程。");
                        var joined = Brep.JoinBreps(flatBreps, tolerance * 4);
                        if (joined == null || joined.Length != 1)
                        {
                            if (joined != null) foreach (var item in joined) item.Dispose();
                            throw new InvalidDataException("展开结果断成多个区域，请分别选择连续的印图区域。");
                        }
                        result.Flat = joined[0];
                        if (!ConnectedSkin(result.Flat))
                            throw new InvalidDataException("展开后表面之间失去公共边，请检查接缝。");
                        double sourceArea = skin.GetArea(), flatArea = result.Flat.GetArea();
                        if (sourceArea <= tolerance * tolerance || Math.Abs(flatArea - sourceArea) / sourceArea > 0.005)
                            throw new InvalidDataException("展开面积变化超过 0.5%，无法保证图案尺寸。请检查是否包含双曲率曲面。");

                        Plane flatPlane;
                        if (!result.Flat.Faces[0].TryGetPlane(out flatPlane, tolerance))
                            throw new InvalidDataException("展开结果不在平面上。");
                        var normalize = Transform.PlaneToPlane(flatPlane, Plane.WorldXY);
                        result.Flat.Transform(normalize);
                        var vertices = CollectFoldPoints(unroller, flatDots, following.Count, normalize, tolerance * 4);
                        result.FlatMesh = result.OriginalMesh.DuplicateMesh();
                        for (int i = 0; i < vertices.Length; i++) result.FlatMesh.Vertices.SetVertex(i, vertices[i]);
                        result.FlatMesh.Normals.ComputeNormals();
                        ValidateFoldMetric(result.OriginalMesh, result.FlatMesh, tolerance * 4);
                        RejectFoldOverlap(result.FlatMesh, tolerance * 4);
                        var flatBounds = result.Flat.GetBoundingBox(true);
                        if (Math.Abs(flatBounds.Min.Z) > tolerance * 4 || Math.Abs(flatBounds.Max.Z) > tolerance * 4)
                            throw new InvalidDataException("部分展开面不在同一平面，请检查折弯连接。");
                        if (Math.Abs(MeshTriangleArea(result.FlatMesh) - flatArea) > Math.Max(flatArea * 0.02, tolerance * tolerance * 10))
                            throw new InvalidDataException("展开网格与刀模面积不符，已停止保存错误对应关系。");
                    }
                    finally
                    {
                        if (flatBreps != null) foreach (var item in flatBreps) item.Dispose();
                        if (curves != null) foreach (var item in curves) item.Dispose();
                        if (flatDots != null) foreach (var item in flatDots) item.Dispose();
                    }
                }
                finally { foreach (var dot in dots) dot.Dispose(); }
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        private static bool ConnectedSkin(Brep brep)
        {
            if (brep.Faces.Count == 0) return false;
            var seen = new HashSet<int> { 0 };
            var queue = new Queue<int>();
            queue.Enqueue(0);
            while (queue.Count != 0)
                foreach (int face in brep.Faces[queue.Dequeue()].AdjacentFaces())
                    if (seen.Add(face)) queue.Enqueue(face);
            return seen.Count == brep.Faces.Count;
        }

        private static Point3d[] CollectFoldPoints(Unroller unroller, TextDot[] dots, int count, Transform normalize, double tolerance)
        {
            var points = new Point3d[count];
            var found = new bool[count];
            foreach (var dot in dots ?? new TextDot[0])
            {
                int index = unroller.FollowingGeometryIndex(dot);
                int label;
                if (index < 0 || index >= count || !int.TryParse(dot.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out label) || label != index)
                    throw new InvalidDataException("展开点编号丢失，无法可靠对应三维原件。");
                var point = normalize * dot.Point;
                if (!point.IsValid || Math.Abs(point.Z) > tolerance || (found[index] && point.DistanceTo(points[index]) > tolerance))
                    throw new InvalidDataException("展开接缝的点出现多个位置。请先切开闭合接缝或拆分印图区域。");
                points[index] = new Point3d(point.X, point.Y, 0);
                found[index] = true;
            }
            if (found.Any(value => !value))
                throw new InvalidDataException("有网格点未能随曲面展开，请检查微小面、接缝和无效几何。");
            return points;
        }

        private static void ValidateFoldMetric(Mesh original, Mesh flat, double tolerance)
        {
            if (original.Vertices.Count != flat.Vertices.Count || original.Faces.Count != flat.Faces.Count)
                throw new InvalidDataException("展开网格拓扑发生变化。");
            for (int i = 0; i < original.Faces.Count; i++)
            {
                var a = original.Faces[i];
                var b = flat.Faces[i];
                if (a.A != b.A || a.B != b.B || a.C != b.C || a.D != b.D)
                    throw new InvalidDataException("展开网格面编号发生变化。");
                int[] ids = a.IsTriangle ? new[] { a.A, a.B, a.C } : new[] { a.A, a.B, a.C, a.D };
                for (int j = 0; j < ids.Length; j++)
                {
                    int first = ids[j], second = ids[(j + 1) % ids.Length];
                    double before = ((Point3d)original.Vertices[first]).DistanceTo(original.Vertices[second]);
                    double after = ((Point3d)flat.Vertices[first]).DistanceTo(flat.Vertices[second]);
                    if (Math.Abs(before - after) > Math.Max(tolerance, before * 0.02))
                        throw new InvalidDataException("局部展开伸缩超过 2%，无法保证折弯处的图案尺寸。");
                }
            }
        }

        private static double MeshTriangleArea(Mesh mesh)
        {
            double area = 0;
            foreach (var face in mesh.Faces)
            {
                var a = (Point3d)mesh.Vertices[face.A];
                var b = (Point3d)mesh.Vertices[face.B];
                var c = (Point3d)mesh.Vertices[face.C];
                area += Vector3d.CrossProduct(b - a, c - a).Length * 0.5;
                if (face.IsQuad)
                {
                    var d = (Point3d)mesh.Vertices[face.D];
                    area += Vector3d.CrossProduct(c - a, d - a).Length * 0.5;
                }
            }
            return area;
        }

        private sealed class FoldTriangle
        {
            public Point2d[] Points;
            public double MinX, MaxX, MinY, MaxY;
        }

        private static void RejectFoldOverlap(Mesh mesh, double tolerance)
        {
            var triangles = new List<FoldTriangle>();
            foreach (var face in mesh.Faces)
            {
                AddFoldTriangle(triangles, mesh, face.A, face.B, face.C);
                if (face.IsQuad) AddFoldTriangle(triangles, mesh, face.A, face.C, face.D);
            }
            triangles.Sort((a, b) => a.MinX.CompareTo(b.MinX));
            for (int i = 0; i < triangles.Count; i++)
                for (int j = i + 1; j < triangles.Count && triangles[j].MinX < triangles[i].MaxX; j++)
                {
                    var a = triangles[i]; var b = triangles[j];
                    if (b.MinY >= a.MaxY || b.MaxY <= a.MinY) continue;
                    if (FoldIntersectionArea(a.Points, b.Points) > tolerance * tolerance)
                        throw new InvalidDataException("展开区域出现重叠，请先沿接缝拆分印图区域，再分别展开。");
                }
        }

        private static void AddFoldTriangle(List<FoldTriangle> triangles, Mesh mesh, int a, int b, int c)
        {
            var points = new[] { a, b, c }.Select(i => new Point2d(mesh.Vertices[i].X, mesh.Vertices[i].Y)).ToArray();
            double signed = FoldCross(points[1] - points[0], points[2] - points[0]);
            if (Math.Abs(signed) < 1e-12) return;
            if (signed < 0) Array.Reverse(points);
            triangles.Add(new FoldTriangle { Points = points,
                MinX = points.Min(p => p.X), MaxX = points.Max(p => p.X),
                MinY = points.Min(p => p.Y), MaxY = points.Max(p => p.Y) });
        }

        private static double FoldCross(Vector2d a, Vector2d b) { return a.X * b.Y - a.Y * b.X; }

        private static double FoldIntersectionArea(Point2d[] a, Point2d[] b)
        {
            var polygon = new List<Point2d>(a);
            for (int edge = 0; edge < 3 && polygon.Count != 0; edge++)
            {
                var start = b[edge]; var direction = b[(edge + 1) % 3] - start;
                var clipped = new List<Point2d>();
                var previous = polygon[polygon.Count - 1];
                double previousSide = FoldCross(direction, previous - start);
                foreach (var point in polygon)
                {
                    double side = FoldCross(direction, point - start);
                    if ((previousSide >= 0) != (side >= 0))
                    {
                        double t = previousSide / (previousSide - side);
                        clipped.Add(previous + t * (point - previous));
                    }
                    if (side >= 0) clipped.Add(point);
                    previous = point; previousSide = side;
                }
                polygon = clipped;
            }
            double area = 0;
            for (int i = 1; i + 1 < polygon.Count; i++)
                area += FoldCross(polygon[i] - polygon[0], polygon[i + 1] - polygon[0]);
            return Math.Abs(area) * 0.5;
        }

        private static string EncodeFold(GeometryBase source, FoldGeometry fold)
        {
            using (var archive = new File3dm())
            {
                archive.Settings.ModelUnitSystem = UnitSystem.Millimeters;
                var original = new ObjectAttributes { Name = "Original" };
                if (source is Extrusion) archive.Objects.AddExtrusion((Extrusion)source, original);
                else if (source is Brep) archive.Objects.AddBrep((Brep)source, original);
                else archive.Objects.AddSurface((Surface)source, original);
                archive.Objects.AddBrep(fold.Flat, new ObjectAttributes { Name = "FlatReference" });
                archive.Objects.AddMesh(fold.OriginalMesh, new ObjectAttributes { Name = "OriginalMesh" });
                archive.Objects.AddMesh(fold.FlatMesh, new ObjectAttributes { Name = "FlatMesh" });
                return Convert.ToBase64String(archive.ToByteArray(new File3dmWriteOptions { Version = 7 }));
            }
        }

        private static ProjectPart ReadFoldPart(RhinoObject source, RhinoObject layout, double tolerance)
        {
            string data = layout.Attributes.GetUserString(FoldArchiveKey);
            if (string.IsNullOrWhiteSpace(data)) throw new InvalidDataException("折弯件缺少随动展开记录，请重新展开。");
            using (var archive = File3dm.FromByteArray(Convert.FromBase64String(data)))
            {
                if (archive == null) throw new InvalidDataException("无法读取折弯对应记录。");
                var objects = archive.Objects.ToDictionary(o => o.Attributes.Name, o => o.Geometry);
                GeometryBase original, reference, originalMesh, flatMesh;
                if (!objects.TryGetValue("Original", out original) || !objects.TryGetValue("FlatReference", out reference) ||
                    !objects.TryGetValue("OriginalMesh", out originalMesh) || !objects.TryGetValue("FlatMesh", out flatMesh) ||
                    !(reference is Brep) || !(originalMesh is Mesh) || !(flatMesh is Mesh))
                    throw new InvalidDataException("折弯件对应记录不完整，请重新展开。");
                if (!GeometryBase.GeometryEquals(source.Geometry, original))
                    throw new InvalidDataException("折弯原件 " + PartName(source, layout) + " 已移动或改变，请重新生成展开关系。");
                Transform registration;
                if (!TryMatchVertices(reference, layout.Geometry, tolerance, out registration) ||
                    !FoldEdgesMatch((Brep)reference, layout.Geometry as Brep, registration, tolerance))
                    throw new InvalidDataException("折弯展开件已缩放、变形或重建，请重新展开。允许整体移动、旋转和镜像。");
                var flat = (Mesh)flatMesh;
                var mapping = (Mesh)originalMesh;
                ValidateFoldMetric(mapping, flat, tolerance * 4);
                var positions = flat.Vertices.Select(p => registration * new Point3d(p.X, p.Y, p.Z)).ToArray();
                if (positions.Any(p => !p.IsValid) || positions.Max(p => p.Z) - positions.Min(p => p.Z) > tolerance)
                    throw new InvalidDataException("折弯展开件必须保持在世界 XY 平面，请只在平面内排版。");
                return new ProjectPart { Source = source, Label = PartName(source, layout),
                    MappingMesh = mapping.DuplicateMesh(), LayoutPoints = positions,
                    Guides = OutlineCurves(layout.Geometry).ToArray() };
            }
        }

        private static bool FoldEdgesMatch(Brep original, Brep current, Transform transform, double tolerance)
        {
            if (current == null || original.Faces.Count != current.Faces.Count || original.Edges.Count != current.Edges.Count) return false;
            for (int e = 0; e < original.Edges.Count; e++)
                for (int i = 0; i <= 8; i++)
                {
                    var a = original.Edges[e].PointAt(original.Edges[e].Domain.ParameterAt(i / 8.0));
                    var b = current.Edges[e].PointAt(current.Edges[e].Domain.ParameterAt(i / 8.0));
                    if ((transform * a).DistanceTo(b) > tolerance) return false;
                }
            return true;
        }
    }
}
