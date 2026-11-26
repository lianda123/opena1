using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;

namespace DielineUV
{
    public sealed partial class DielineUVCommand
    {
        private const string NeutralBendKind = "NeutralBendStretch.v1";

        private sealed class BendDirection
        {
            public Transform Transform;
            public double Error, ScaleX, ScaleY;
        }

        private static bool HasBentSkin(GeometryBase geometry)
        {
            using (var brep = geometry is Brep original ? original.DuplicateBrep() : Brep.TryConvertBrep(geometry))
            {
                if (brep == null) return false;
                var largest = brep.Faces.OrderByDescending(face => {
                    using (var area = AreaMassProperties.Compute(face)) return area?.Area ?? 0;
                }).FirstOrDefault();
                return largest != null && !largest.IsPlanar(StoredGeometryTolerance);
            }
        }

        private static ProjectPart BindNeutralBend(RhinoDoc doc, RhinoObject source, Curve[] guides,
            Guid[] layoutIds, Canvas frame, double tolerance)
        {
            using (var brep = source.Geometry is Brep original ? original.DuplicateBrep() : Brep.TryConvertBrep(source.Geometry))
            {
                if (brep == null) throw new InvalidDataException("折弯绑定需要曲面、拉伸体或多重曲面。");
                RhinoApp.WriteLine("请选择实际印刷的一侧连续表面，包含相连的直面和弯曲面；这里选实体表面，不选中间层曲面。");
                int[] primary = brep.Faces.Count == 1 ? new[] { 0 } : PickBendFaces(source, brep, new int[0], false,
                    "选择实际印刷一侧的连续表面（Ctrl+Shift；Mac 用 Command+Shift），回车确认");
                if (primary == null) return null;
                if (primary.Length == 0 || primary.Any(i => i < 0 || i >= brep.Faces.Count))
                    throw new InvalidDataException("印刷面编号无效，请把拉伸体用 ConvertExtrusion 转成多重曲面后选择。");
                using (var skin = brep.DuplicateSubBrep(primary))
                {
                if (skin == null || skin.IsSolid || !ConnectedSkin(skin))
                    throw new InvalidDataException("只选择印刷一侧的连续曲面，不同时选择背面和厚度侧面。");
                using (var fold = NeutralRibbonOrUnroll(source, brep, primary, skin, doc.ModelAbsoluteTolerance))
                {
                    var loops = JoinLayoutLoops(guides, tolerance);
                    try
                    {
                        var outer = LargestClosedCurve(loops, tolerance);
                        if (outer == null) throw new InvalidDataException("已有刀模需要封闭的最外轮廓；多条 Make2D 线可以一起选择。");
                        outer.Transform(Transform.Translation(0, 0, guides[0].GetBoundingBox(true).Center.Z));
                        Transform registration;
                        if (!ChooseBendDirection(doc, fold, outer, frame, tolerance, out registration)) return null;
                        var opposite = fold.BackFaces ?? InferBendBackFaces(brep, primary, skin, tolerance);
                        if (brep.IsSolid && opposite.Length == 0)
                        {
                            RhinoApp.WriteLine("未能自动识别背面，请补选这件的背面；回车可跳过，未选的面使用侧面材质。");
                            opposite = PickBendFaces(source, brep, primary, true,
                                "选该折弯件的背面（Ctrl+Shift；Mac 用 Command+Shift），回车确认或跳过");
                            if (opposite == null) return null;
                        }
                        RhinoApp.WriteLine("已记录印刷面 {0} 个、背面 {1} 个，其他面作为厚度侧边。", primary.Length, opposite.Length);
                        var mesh = fold.OriginalMesh.DuplicateMesh();
                        var positions = fold.FlatMesh.Vertices.Select(v => registration * new Point3d(v.X, v.Y, v.Z)).ToList();
                        try
                        {
                            // Extend the chart to the opposite skin as well, so both-sided
                            // printing uses the same station on the bend, rather than a planar projection.
                            foreach (var face in brep.Faces.Where(face => !primary.Contains(face.FaceIndex)))
                                using (var single = face.DuplicateFace(false))
                                {
                                    var pieces = Mesh.CreateFromBrep(single, MeshingParameters.QualityRenderMesh);
                                    if (pieces == null) throw new InvalidDataException("无法读取折弯件的非印刷面。");
                                    try
                                    {
                                        foreach (var piece in pieces)
                                        {
                                            foreach (var vertex in piece.Vertices)
                                            {
                                                if (fold.ProjectPoint != null)
                                                {
                                                    positions.Add(registration * fold.ProjectPoint(new Point3d(vertex.X, vertex.Y, vertex.Z)));
                                                    continue;
                                                }
                                                var exact = skin.ClosestPoint(new Point3d(vertex.X, vertex.Y, vertex.Z));
                                                if (!exact.IsValid) throw new InvalidDataException("背面无法投影到印刷曲面。");
                                                var nearest = fold.OriginalMesh.ClosestMeshPoint(exact, 0);
                                                if (nearest == null) throw new InvalidDataException("缺少折弯展开的对应点。");
                                                var f = fold.OriginalMesh.Faces[nearest.FaceIndex];
                                                var t = nearest.T;
                                                var ids = new[] { f.A, f.B, f.C, f.D };
                                                double x = 0, y = 0, z = 0;
                                                for (int k = 0; k < (f.IsTriangle ? 3 : 4); k++)
                                                {
                                                    var p = fold.FlatMesh.Vertices[ids[k]];
                                                    x += p.X * t[k]; y += p.Y * t[k]; z += p.Z * t[k];
                                                }
                                                positions.Add(registration * new Point3d(x, y, z));
                                            }
                                            mesh.Append(piece);
                                        }
                                    }
                                    finally { foreach (var piece in pieces) piece.Dispose(); }
                                }
                            if (positions.Count != mesh.Vertices.Count ||
                                positions.Any(p => !InsideFrame(new BoundingBox(p, p), frame, tolerance)))
                                throw new InvalidDataException("折弯件的展开范围超出外框，请检查所选刀模及方向。");
                            mesh.Normals.ComputeNormals();
                            var part = new ProjectPart { Source = source, BoundSourceId = source.Id,
                                Label = PartName(source, source), Guides = guides, LayoutIds = layoutIds,
                                MappingMesh = mesh, LayoutPoints = positions.ToArray(), PrintFaces = primary,
                                BackFaces = opposite, MappingKind = NeutralBendKind };
                            mesh = null;
                            return part;
                        }
                        finally { mesh?.Dispose(); }
                    }
                    finally { foreach (var loop in loops) loop.Dispose(); }
                }
                }
            }
        }

        private static FoldGeometry NeutralRibbonOrUnroll(RhinoObject source, Brep body, int[] primary, Brep skin, double tolerance)
        {
            var extrusion = source.Geometry as Extrusion;
            if (extrusion != null && extrusion.ProfileCount == 1)
                using (var profile = extrusion.Profile3d(0, 0.0))
                {
                    var segments = profile?.DuplicateSegments();
                    try
                    {
                        if (segments != null && segments.Length == 4)
                        {
                            var rails = segments.OrderByDescending(curve => curve.GetLength()).Take(2).ToArray();
                            bool reverse = rails[0].PointAtStart.DistanceTo(rails[1].PointAtEnd) + rails[0].PointAtEnd.DistanceTo(rails[1].PointAtStart) <
                                rails[0].PointAtStart.DistanceTo(rails[1].PointAtStart) + rails[0].PointAtEnd.DistanceTo(rails[1].PointAtEnd);
                            var centers = new List<double[]>();
                            var widths = new List<double>();
                            for (int i = 0; i <= 2400; i++)
                            {
                                double fraction = i / 2400.0;
                                var a = rails[0].PointAt(rails[0].Domain.ParameterAt(fraction));
                                var b = rails[1].PointAt(rails[1].Domain.ParameterAt(reverse ? 1 - fraction : fraction));
                                var center = (a + b) * .5;
                                centers.Add(new[] { center.X, center.Y, center.Z });
                                widths.Add(a.DistanceTo(b));
                            }
                            var axis = extrusion.PathEnd - extrusion.PathStart;
                            double height = axis.Length;
                            if (widths.Min() > tolerance && widths.Max() / widths.Min() < 1.2 &&
                                widths.Max() < rails.Min(rail => rail.GetLength()) * .25 && height > tolerance && axis.Unitize())
                            {
                                var stations = new RibbonStations(centers.ToArray());
                                var result = new FoldGeometry();
                                try
                                {
                                    var parameters = MeshingParameters.QualityRenderMesh;
                                    parameters.RefineAngle = Math.PI / 90;
                                    parameters.MaximumEdgeLength = Math.Max(skin.GetBoundingBox(true).Diagonal.Length / 32, tolerance * 10);
                                    var pieces = Mesh.CreateFromBrep(skin, parameters);
                                    if (pieces == null || pieces.Length == 0) throw new InvalidDataException("无法读取弯板印刷曲面。");
                                    result.OriginalMesh = new Mesh();
                                    foreach (var piece in pieces)
                                        using (piece) result.OriginalMesh.Append(piece);
                                    result.FlatMesh = result.OriginalMesh.DuplicateMesh();
                                    var origin = extrusion.PathStart;
                                    result.ProjectPoint = p =>
                                    {
                                        double along = (p - origin) * axis;
                                        var basePoint = p - along * axis;
                                        return new Point3d(Math.Max(0, Math.Min(height, along)),
                                            stations.ClosestLength(basePoint.X, basePoint.Y, basePoint.Z), 0);
                                    };
                                    for (int i = 0; i < result.FlatMesh.Vertices.Count; i++)
                                    {
                                        var p = result.OriginalMesh.Vertices.Point3dAt(i);
                                        result.FlatMesh.Vertices.SetVertex(i, result.ProjectPoint(p));
                                    }
                                    var railDistances = new double[2];
                                    for (int i = 0; i < Math.Min(64, result.OriginalMesh.Vertices.Count); i++)
                                    {
                                        var p = result.OriginalMesh.Vertices.Point3dAt(i);
                                        p -= ((p - origin) * axis) * axis;
                                        for (int k = 0; k < 2; k++)
                                        {
                                            double t;
                                            if (rails[k].ClosestPoint(p, out t)) railDistances[k] += p.DistanceTo(rails[k].PointAt(t));
                                        }
                                    }
                                    var oppositeRail = rails[railDistances[0] <= railDistances[1] ? 1 : 0];
                                    result.BackFaces = body.Faces.Where(face => !primary.Contains(face.FaceIndex)).Where(face =>
                                    {
                                        double u = face.Domain(0).Mid, v = face.Domain(1).Mid;
                                        var normal = face.NormalAt(u, v);
                                        if (!normal.Unitize() || Math.Abs(normal * axis) > .1) return false;
                                        var p = face.PointAt(u, v);
                                        p -= ((p - origin) * axis) * axis;
                                        double t;
                                        return oppositeRail.ClosestPoint(p, out t) &&
                                            p.DistanceTo(oppositeRail.PointAt(t)) <= Math.Max(tolerance * 4, StoredGeometryTolerance * 4);
                                    }).Select(face => face.FaceIndex).ToArray();
                                    result.FlatMesh.Normals.ComputeNormals();
                                    result.Flat = Brep.CreateFromCornerPoints(Point3d.Origin, new Point3d(height, 0, 0),
                                        new Point3d(height, stations.Length, 0), new Point3d(0, stations.Length, 0), tolerance);
                                    if (result.Flat == null) throw new InvalidDataException("中线展开范围无效。");
                                    RhinoApp.WriteLine("已沿弯板双轨中线建立对应：中线长 {0:F3} mm，拉伸宽 {1:F3} mm。", stations.Length, height);
                                    return result;
                                }
                                catch { result.Dispose(); throw; }
                            }
                        }
                    }
                    finally { if (segments != null) foreach (var segment in segments) segment.Dispose(); }
                }
            RhinoApp.WriteLine("按所选印刷曲面展开，再伸缩到已有中间层刀模；图案允许拉伸。");
            return UnrollFold(skin, tolerance);
        }

        private static int[] InferBendBackFaces(Brep brep, int[] primary, Brep skin, double tolerance)
        {
            var result = new List<int>();
            foreach (var face in brep.Faces.Where(face => !primary.Contains(face.FaceIndex)))
            {
                int valid = 0, parallel = 0;
                foreach (double fu in new[] { .2, .5, .8 })
                    foreach (double fv in new[] { .2, .5, .8 })
                    {
                        double u = face.Domain(0).ParameterAt(fu), v = face.Domain(1).ParameterAt(fv);
                        if (face.IsPointOnFace(u, v) == PointFaceRelation.Exterior) continue;
                        var point = face.PointAt(u, v);
                        Point3d closest; ComponentIndex component; double s, t; Vector3d normal;
                        if (!skin.ClosestPoint(point, out closest, out component, out s, out t, 0, out normal) ||
                            component.ComponentIndexType != ComponentIndexType.BrepFace) continue;
                        valid++;
                        var direction = face.NormalAt(u, v);
                        if (point.DistanceTo(closest) > tolerance && direction.Unitize() && normal.Unitize() &&
                            Math.Abs(direction * normal) > .95) parallel++;
                    }
                if (valid >= 3 && parallel >= Math.Ceiling(valid * .75)) result.Add(face.FaceIndex);
            }
            return result.ToArray();
        }

        private static int[] PickBendFaces(RhinoObject source, Brep brep, int[] excluded, bool allowNone, string prompt)
        {
            while (true)
            using (var picker = new GetObject())
            {
                picker.SetCommandPrompt(prompt);
                picker.GeometryFilter = ObjectType.Surface;
                picker.SubObjectSelect = true;
                picker.GroupSelect = false;
                picker.EnablePreSelect(false, true);
                picker.AcceptNothing(allowNone);
                picker.SetCustomGeometryFilter((obj, geometry, component) => obj.Id == source.Id);
                var result = picker.GetMultiple(allowNone ? 0 : 1, 0);
                if (result == GetResult.Nothing && allowNone) return new int[0];
                if (result != GetResult.Object) return null;
                var indices = new HashSet<int>();
                for (int i = 0; i < picker.ObjectCount; i++)
                    using (var reference = picker.Object(i))
                    {
                        var face = reference.Face();
                        if (face == null) continue;
                        int index = -1;
                        if (source.Geometry is Brep) index = face.FaceIndex;
                        else
                            using (var selected = face.DuplicateFace(false))
                            {
                                var bounds = selected.GetBoundingBox(true);
                                foreach (var candidate in brep.Faces)
                                    using (var copy = candidate.DuplicateFace(false))
                                    {
                                        var test = copy.GetBoundingBox(true);
                                        if (bounds.Min.DistanceTo(test.Min) <= StoredGeometryTolerance &&
                                            bounds.Max.DistanceTo(test.Max) <= StoredGeometryTolerance)
                                        { index = candidate.FaceIndex; break; }
                                    }
                            }
                        if (index >= 0 && index < brep.Faces.Count && !excluded.Contains(index)) indices.Add(index);
                    }
                if (indices.Count == 0 && !allowNone)
                { RhinoApp.WriteLine("没有识别到该原件的印刷面，请重新点选曲面。"); continue; }
                return indices.OrderBy(i => i).ToArray();
            }
        }

        private static bool ChooseBendDirection(RhinoDoc doc, FoldGeometry fold, Curve target, Canvas frame,
            double tolerance, out Transform registration)
        {
            registration = Transform.Unset;
            var flatPoints = fold.FlatMesh.Vertices.Select(v => new Point3d(v.X, v.Y, v.Z)).ToArray();
            var sourceBox = StretchBox.Fit(flatPoints.Select(p => new[] { p.X, p.Y }).ToArray());
            var targetBox = StretchBox.Fit(SampleOutline(target).Concat(BindingCurveCorners(target)).Select(p => new[] { p.X, p.Y }).ToArray());
            var bounds = target.GetBoundingBox(true);
            var outline = OutlineCurves(fold.Flat).ToArray();
            var samples = outline.SelectMany(SampleOutline).ToArray();
            var matches = new List<BendDirection>();
            try
            {
                foreach (bool quarter in new[] { false, true })
                    foreach (int sx in new[] { 1, -1 })
                        foreach (int sy in new[] { 1, -1 })
                        {
                            var m = sourceBox.To(targetBox, quarter, sx, sy);
                            var transform = Transform.Identity;
                            transform.M00 = m[0]; transform.M01 = m[1]; transform.M03 = m[2];
                            transform.M10 = m[3]; transform.M11 = m[4]; transform.M13 = m[5];
                            transform.M23 = bounds.Center.Z;
                            var placed = flatPoints.Select(p => transform * p).ToArray();
                            if (!InsideFrame(new BoundingBox(placed), frame, tolerance)) continue;
                            double error = 0;
                            foreach (var p in samples)
                            {
                                var q = transform * p; double parameter;
                                if (!target.ClosestPoint(q, out parameter)) { error = double.PositiveInfinity; break; }
                                double distance = q.DistanceTo(target.PointAt(parameter)); error += distance * distance;
                            }
                            matches.Add(new BendDirection { Transform = transform,
                                Error = Math.Sqrt(error / Math.Max(1, samples.Length)),
                                ScaleX = quarter ? targetBox.Height / sourceBox.Width : targetBox.Width / sourceBox.Width,
                                ScaleY = quarter ? targetBox.Width / sourceBox.Height : targetBox.Height / sourceBox.Height });
                        }
                if (matches.Count == 0) throw new InvalidDataException("没有可放入外框的折弯方向，请检查刀模和外框。");
                matches = matches.OrderBy(item => item.Error / Math.Max(tolerance, bounds.Diagonal.Length) +
                    .01 * (Math.Pow(Math.Log(item.ScaleX), 2) + Math.Pow(Math.Log(item.ScaleY), 2))).ToList();
                var markerIndices = new List<int>();
                foreach (var corner in new[] { new[] { -.5, -.5 }, new[] { .5, -.5 }, new[] { -.5, .5 } })
                {
                    var p = new Point3d(sourceBox.X + sourceBox.UX * corner[0] * sourceBox.Width - sourceBox.UY * corner[1] * sourceBox.Height,
                        sourceBox.Y + sourceBox.UY * corner[0] * sourceBox.Width + sourceBox.UX * corner[1] * sourceBox.Height, 0);
                    markerIndices.Add(Enumerable.Range(0, flatPoints.Length).Where(i => !markerIndices.Contains(i))
                        .OrderBy(i => flatPoints[i].DistanceToSquared(p)).First());
                }
                using (var preview = new BendPreview { Document = doc, Fold = fold, Indices = markerIndices.ToArray(), Curves = outline })
                {
                    preview.Enabled = true;
                    int index = 0;
                    while (true)
                    {
                        var match = matches[index]; preview.Registration = match.Transform; doc.Views.Redraw();
                        using (var picker = new GetOption())
                        {
                            picker.SetCommandPrompt(string.Format(CultureInfo.InvariantCulture,
                                "折弯方向 {0}/{1}，伸缩 {2:F1}% / {3:F1}%，轮廓拟合差 {4:F3}mm。核对两边编号，回车采用",
                                index + 1, matches.Count, match.ScaleX * 100, match.ScaleY * 100, match.Error));
                            int next = picker.AddOption(new LocalizeStringPair("Next", "下一个方向"));
                            int previous = picker.AddOption(new LocalizeStringPair("Previous", "上一个方向"));
                            picker.AcceptNothing(true);
                            var result = picker.Get();
                            if (result == GetResult.Nothing) { registration = match.Transform; return true; }
                            if (result != GetResult.Option) return false;
                            index = (index + (picker.Option().Index == next ? 1 : picker.Option().Index == previous ? -1 : 0) + matches.Count) % matches.Count;
                        }
                    }
                }
            }
            finally { doc.Views.Redraw(); foreach (var curve in outline) curve.Dispose(); }
        }

        private sealed class BendPreview : DisplayConduit, IDisposable
        {
            public RhinoDoc Document;
            public FoldGeometry Fold;
            public int[] Indices;
            public Curve[] Curves;
            public Transform Registration;
            protected override void DrawForeground(DrawEventArgs e)
            {
                if (e.RhinoDoc?.RuntimeSerialNumber != Document.RuntimeSerialNumber) return;
                for (int i = 0; i < Indices.Length; i++)
                {
                    int index = Indices[i];
                    e.Display.DrawDot(Fold.OriginalMesh.Vertices.Point3dAt(index), (i + 1).ToString(), Color.DodgerBlue, Color.White);
                    e.Display.DrawDot(Registration * Fold.FlatMesh.Vertices.Point3dAt(index), (i + 1).ToString(), Color.ForestGreen, Color.White);
                }
                foreach (var curve in Curves)
                    using (var copy = curve.DuplicateCurve()) { copy.Transform(Registration); e.Display.DrawCurve(copy, Color.LimeGreen, 2); }
            }
            public void Dispose() { Enabled = false; }
        }
    }
}
