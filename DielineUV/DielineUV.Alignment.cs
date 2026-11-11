using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;

namespace DielineUV
{
    public sealed partial class DielineUVCommand
    {
        private sealed class BoardOutline : IDisposable
        {
            public Plane Plane;
            public Curve Outer;
            public Curve[] Holes;

            public void Dispose()
            {
                Outer.Dispose();
                foreach (var hole in Holes) hole.Dispose();
            }
        }

        private static BoardOutline ReadBoardOutline(GeometryBase geometry, double tolerance)
        {
            if (geometry is Mesh mesh)
            {
                Plane plane;
                double deviation;
                if (Plane.FitPlaneToPoints(Vertices(mesh), out plane, out deviation) != PlaneFitResult.Success ||
                    deviation > tolerance) return null;
                var loops = OutlineCurves(mesh).ToArray();
                var outer = LargestClosedCurve(loops, tolerance);
                if (outer != null)
                    return new BoardOutline { Plane = plane, Outer = outer, Holes = loops.Where(c => c != outer).ToArray() };
                foreach (var loop in loops) loop.Dispose();
                return null;
            }

            var brep = geometry as Brep;
            var converted = brep == null ? Brep.TryConvertBrep(geometry) : null;
            BoardOutline best = null;
            double largest = 0;
            try
            {
                brep = brep ?? converted;
                if (brep == null) return null;
                foreach (var face in brep.Faces)
                {
                    Plane plane;
                    if (!face.TryGetPlane(out plane, tolerance) || face.OuterLoop == null) continue;
                    var outer = face.OuterLoop.To3dCurve();
                    Point3d center;
                    double area;
                    if (!MeasureOutline(outer, tolerance, out center, out area) || area <= largest)
                    {
                        outer?.Dispose();
                        continue;
                    }
                    best?.Dispose();
                    best = new BoardOutline
                    {
                        Plane = plane,
                        Outer = outer,
                        Holes = face.Loops.Where(loop => loop.LoopType == BrepLoopType.Inner)
                            .Select(loop => loop.To3dCurve()).Where(curve => curve != null).ToArray()
                    };
                    largest = area;
                }
                return best;
            }
            finally { converted?.Dispose(); }
        }

        private static bool MeasureOutline(Curve curve, double tolerance, out Point3d center, out double area)
        {
            center = Point3d.Unset;
            area = 0;
            if (curve == null || !curve.IsClosed) return false;
            using (var properties = AreaMassProperties.Compute(curve, tolerance))
            {
                if (properties == null) return false;
                center = properties.Centroid;
                area = Math.Abs(properties.Area);
                return center.IsValid && area > tolerance * tolerance;
            }
        }

        private static Curve LargestClosedCurve(IEnumerable<Curve> curves, double tolerance)
        {
            Curve largest = null;
            double largestArea = 0;
            foreach (var curve in curves)
            {
                Point3d center;
                double area;
                if (!MeasureOutline(curve, tolerance, out center, out area) || area <= largestArea) continue;
                largest = curve;
                largestArea = area;
            }
            return largest;
        }

        private static Curve[] JoinLayoutLoops(Curve[] guides, double tolerance)
        {
            var closed = new List<Curve>();
            var open = new List<Curve>();
            try
            {
                foreach (var guide in guides)
                {
                    var bounds = guide.GetBoundingBox(true);
                    if (!bounds.IsValid || bounds.Max.Z - bounds.Min.Z > tolerance) continue;
                    var curve = guide.DuplicateCurve();
                    if (!curve.Transform(Transform.Translation(0, 0, -bounds.Center.Z)) || curve.GetLength() <= tolerance)
                    {
                        curve.Dispose();
                        continue;
                    }
                    (curve.IsClosed ? closed : open).Add(curve);
                }
                if (open.Count != 0)
                    foreach (var curve in Curve.JoinCurves(open, tolerance) ?? new Curve[0])
                        if (curve.IsClosed) closed.Add(curve);
                        else curve.Dispose();
                return closed.ToArray();
            }
            finally { foreach (var curve in open) curve.Dispose(); }
        }

        private static List<Transform> FindBoardAlignments(GeometryBase source, Curve[] guides, double tolerance,
            out Point3d[] markers)
        {
            markers = new Point3d[0];
            var matches = new List<Transform>();
            using (var board = ReadBoardOutline(source, tolerance))
            {
                if (board == null) return matches;
                var targetLoops = JoinLayoutLoops(guides, tolerance);
                var sourceHoles = new List<Curve>();
                try
                {
                    var target = LargestClosedCurve(targetLoops, tolerance);
                    if (target == null) return matches;
                    var targetHoles = targetLoops.Where(curve => curve != target).ToArray();
                    if (targetHoles.Any(hole => Curve.PlanarClosedCurveRelationship(
                        target, hole, Plane.WorldXY, tolerance) != RegionContainment.BInsideA))
                        return matches;
                    var toPlane = Transform.PlaneToPlane(board.Plane, Plane.WorldXY);
                    using (var outer = board.Outer.DuplicateCurve())
                    {
                        if (!outer.Transform(toPlane) || Math.Abs(outer.GetLength() - target.GetLength()) > tolerance * 4)
                            return matches;
                        Point3d sourceCenter, targetCenter;
                        double sourceArea, targetArea;
                        if (!MeasureOutline(outer, tolerance, out sourceCenter, out sourceArea) ||
                            !MeasureOutline(target, tolerance, out targetCenter, out targetArea) ||
                            Math.Abs(sourceArea - targetArea) > tolerance * target.GetLength())
                            return matches;

                        foreach (var hole in board.Holes)
                        {
                            var projected = hole.DuplicateCurve();
                            if (projected.Transform(toPlane)) sourceHoles.Add(projected);
                            else projected.Dispose();
                        }
                        Point3d sourceAnchor;
                        Point3d[] targetAnchors;
                        Circle sourceCircle, targetCircle;
                        if (outer.TryGetCircle(out sourceCircle, tolerance) && target.TryGetCircle(out targetCircle, tolerance))
                        {
                            sourceAnchor = HoleCenters(sourceHoles, tolerance)
                                .Where(center => center.DistanceTo(sourceCenter) > tolerance)
                                .OrderByDescending(center => center.DistanceToSquared(sourceCenter))
                                .DefaultIfEmpty(Point3d.Unset).First();
                            double radius = sourceAnchor.IsValid ? sourceAnchor.DistanceTo(sourceCenter) : 0;
                            targetAnchors = HoleCenters(targetHoles, tolerance)
                                .Where(center => radius > tolerance && Math.Abs(center.DistanceTo(targetCenter) - radius) <= tolerance)
                                .ToArray();
                            if (targetAnchors.Length == 0)
                            {
                                sourceAnchor = sourceCenter + Vector3d.XAxis;
                                targetAnchors = new[] { targetCenter + Vector3d.XAxis, targetCenter + Vector3d.YAxis,
                                    targetCenter - Vector3d.XAxis, targetCenter - Vector3d.YAxis };
                            }
                        }
                        else
                        {
                            sourceAnchor = FarthestOutlinePoints(outer, sourceCenter, tolerance).FirstOrDefault();
                            double radius = sourceAnchor.DistanceTo(sourceCenter);
                            targetAnchors = FarthestOutlinePoints(target, targetCenter, tolerance)
                                .Where(point => Math.Abs(point.DistanceTo(targetCenter) - radius) <= tolerance * 2).ToArray();
                        }
                        if (sourceAnchor.DistanceTo(sourceCenter) <= tolerance || targetAnchors.Length == 0) return matches;

                        markers = new[] { board.Outer.PointAtStart, board.Outer.PointAtNormalizedLength(0.27),
                            board.Outer.PointAtNormalizedLength(0.61) };
                        var sourcePoints = SampleOutline(outer);
                        var targetPoints = SampleOutline(target);
                        var targetBounds = target.GetBoundingBox(true);
                        int bestHoleCount = -1;
                        foreach (var anchor in targetAnchors)
                            for (int mirrored = 0; mirrored < 2; mirrored++)
                            {
                                var direction = sourceAnchor - sourceCenter;
                                var targetDirection = anchor - targetCenter;
                                double sourceAngle = Math.Atan2(mirrored == 0 ? direction.Y : -direction.Y, direction.X);
                                double targetAngle = Math.Atan2(targetDirection.Y, targetDirection.X);
                                var reflection = mirrored == 0 ? Transform.Identity
                                    : Transform.Mirror(new Plane(Point3d.Origin, Vector3d.YAxis));
                                var alignment = Transform.Translation(targetCenter - Point3d.Origin) *
                                    Transform.Rotation(targetAngle - sourceAngle, Vector3d.ZAxis, Point3d.Origin) *
                                    reflection * Transform.Translation(Point3d.Origin - sourceCenter);
                                var worldAlignment = alignment * toPlane;
                                if (!SameOutline(outer, target, sourcePoints, targetPoints, alignment, tolerance) ||
                                    !SameXYBounds(source.GetBoundingBox(worldAlignment), targetBounds, tolerance * 2))
                                    continue;
                                int holeCount = CountMatchingHoles(sourceHoles, targetHoles, alignment, tolerance);
                                if (holeCount < bestHoleCount) continue;
                                if (holeCount > bestHoleCount)
                                {
                                    matches.Clear();
                                    bestHoleCount = holeCount;
                                }
                                if (!SameAlignment(matches, worldAlignment, markers, tolerance))
                                    matches.Add(worldAlignment);
                            }
                        return matches;
                    }
                }
                finally
                {
                    foreach (var curve in targetLoops) curve.Dispose();
                    foreach (var curve in sourceHoles) curve.Dispose();
                }
            }
        }

        private static Point3d[] HoleCenters(IEnumerable<Curve> curves, double tolerance)
        {
            var centers = new List<Point3d>();
            foreach (var curve in curves)
            {
                Point3d center;
                double area;
                if (MeasureOutline(curve, tolerance, out center, out area)) centers.Add(center);
            }
            return centers.ToArray();
        }

        private static Point3d[] FarthestOutlinePoints(Curve curve, Point3d center, double tolerance)
        {
            var domain = curve.Domain;
            var parameters = (curve.DivideByCount(128, true) ?? new double[0])
                .Where(t => t > domain.T0 && t < domain.T1).Concat(new[] { domain.T0 }).Distinct().OrderBy(t => t).ToArray();
            var radii = parameters.Select(t => curve.PointAt(t).DistanceToSquared(center)).ToArray();
            var peaks = BindingCurveCorners(curve).ToList();
            for (int i = 0; i < parameters.Length; i++)
            {
                int previous = (i + parameters.Length - 1) % parameters.Length;
                int next = (i + 1) % parameters.Length;
                if (radii[i] < radii[previous] || radii[i] < radii[next]) continue;
                double left = parameters[previous] - (i == 0 ? domain.Length : 0);
                double right = parameters[next] + (i == parameters.Length - 1 ? domain.Length : 0);
                // Refine smooth extrema between the equal-length samples.
                for (int step = 0; step < 40; step++)
                {
                    double a = left + (right - left) / 3;
                    double b = right - (right - left) / 3;
                    if (ClosedCurvePoint(curve, a).DistanceToSquared(center) < ClosedCurvePoint(curve, b).DistanceToSquared(center))
                        left = a;
                    else right = b;
                }
                peaks.Add(curve.PointAt(parameters[i]));
                peaks.Add(ClosedCurvePoint(curve, (left + right) / 2));
            }
            var result = new List<Point3d>();
            double farthest = peaks.Max(point => point.DistanceTo(center));
            foreach (var point in peaks.OrderByDescending(candidate => candidate.DistanceToSquared(center)))
                if (point.DistanceTo(center) >= farthest - tolerance &&
                    !result.Any(existing => existing.DistanceTo(point) <= tolerance))
                    result.Add(point);
            return result.Take(48).ToArray();
        }

        private static Point3d ClosedCurvePoint(Curve curve, double parameter)
        {
            var domain = curve.Domain;
            double fraction = (parameter - domain.T0) / domain.Length;
            return curve.PointAt(domain.ParameterAt(fraction - Math.Floor(fraction)));
        }

        private static Point3d[] SampleOutline(Curve curve)
        {
            Point3d[] points;
            curve.DivideByCount(128, true, out points);
            return (points ?? new Point3d[0]).Concat(BindingCurveCorners(curve)).Distinct().ToArray();
        }

        private static bool SameOutline(Curve source, Curve target, Point3d[] sourcePoints, Point3d[] targetPoints,
            Transform alignment, double tolerance)
        {
            Transform inverse;
            if (!alignment.TryGetInverse(out inverse)) return false;
            foreach (var point in sourcePoints)
            {
                double parameter;
                if (!target.ClosestPoint(alignment * point, out parameter, tolerance)) return false;
            }
            foreach (var point in targetPoints)
            {
                double parameter;
                if (!source.ClosestPoint(inverse * point, out parameter, tolerance)) return false;
            }
            return true;
        }

        private static bool SameXYBounds(BoundingBox a, BoundingBox b, double tolerance)
        {
            return a.IsValid && b.IsValid && Math.Abs(a.Min.X - b.Min.X) <= tolerance &&
                Math.Abs(a.Min.Y - b.Min.Y) <= tolerance && Math.Abs(a.Max.X - b.Max.X) <= tolerance &&
                Math.Abs(a.Max.Y - b.Max.Y) <= tolerance;
        }

        private static int CountMatchingHoles(List<Curve> source, Curve[] target, Transform alignment, double tolerance)
        {
            int count = 0;
            foreach (var hole in target)
            {
                var targetBounds = hole.GetBoundingBox(true);
                foreach (var original in source)
                {
                    if (!SameXYBounds(original.GetBoundingBox(alignment), targetBounds, tolerance)) continue;
                    if (!SameOutline(original, hole, SampleOutline(original), SampleOutline(hole), alignment, tolerance)) continue;
                    count++;
                    break;
                }
            }
            return count;
        }

        private static bool SameAlignment(IEnumerable<Transform> matches, Transform candidate, Point3d[] markers, double tolerance)
        {
            return matches.Any(existing => markers.All(point =>
                (existing * point).DistanceTo(candidate * point) <= tolerance));
        }

        private sealed class AlignmentPreview : DisplayConduit
        {
            public Point3d[] Markers;
            public Transform ToLayout;
            public double LayoutZ;

            protected override void DrawForeground(DrawEventArgs args)
            {
                for (int i = 0; i < Markers.Length; i++)
                {
                    string label = (i + 1).ToString();
                    var layout = ToLayout * Markers[i];
                    layout.Z = LayoutZ;
                    args.Display.DrawDot(Markers[i], label, Color.SteelBlue, Color.White);
                    args.Display.DrawDot(layout, label, Color.SeaGreen, Color.White);
                }
            }
        }

        private static bool ChooseBoardAlignment(RhinoDoc doc, List<Transform> matches, Point3d[] markers,
            double layoutZ, out Transform alignment)
        {
            alignment = matches[0];
            if (matches.Count == 1) return true;
            var preview = new AlignmentPreview { Markers = markers, LayoutZ = layoutZ };
            int index = 0;
            try
            {
                preview.Enabled = true;
                while (true)
                {
                    preview.ToLayout = matches[index];
                    doc.Views.Redraw();
                    using (var picker = new GetOption())
                    {
                        picker.SetCommandPrompt(string.Format("方向 {0}/{1}。对照原件和刀模上的编号，回车采用当前方向",
                            index + 1, matches.Count));
                        int next = picker.AddOption(new LocalizeStringPair("Next", "下一个方向"));
                        int previous = picker.AddOption(new LocalizeStringPair("Previous", "上一个方向"));
                        picker.AcceptNothing(true);
                        picker.AcceptEnterWhenDone(true);
                        var result = picker.Get();
                        if (result == GetResult.Nothing)
                        {
                            alignment = matches[index];
                            return true;
                        }
                        if (result != GetResult.Option) return false;
                        if (picker.Option().Index == next) index = (index + 1) % matches.Count;
                        else if (picker.Option().Index == previous) index = (index + matches.Count - 1) % matches.Count;
                    }
                }
            }
            finally
            {
                preview.Enabled = false;
                doc.Views.Redraw();
            }
        }
    }
}
