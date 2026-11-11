using System;
using Rhino.Geometry;

namespace DielineUV
{
    public sealed partial class DielineUVCommand
    {
        // Project mappings use millimeters. Ignore differences smaller than one micron.
        private const double StoredGeometryTolerance = 0.001;
        private const double NurbsParameterTolerance = 1e-9;

        private static string SavedGeometryDifference(GeometryBase current, GeometryBase recorded)
        {
            if (current == null || recorded == null) return "缺少几何记录";
            if (GeometryBase.GeometryEquals(current, recorded)) return null;

            double tolerance = StoredGeometryTolerance;
            var currentBounds = current.GetBoundingBox(true);
            var savedBounds = recorded.GetBoundingBox(true);
            if (!currentBounds.IsValid || !savedBounds.IsValid) return "的三维范围无法读取";
            if (currentBounds.Min.DistanceTo(savedBounds.Min) > tolerance ||
                currentBounds.Max.DistanceTo(savedBounds.Max) > tolerance)
                return "的三维范围与导出记录不同";

            if (current is Mesh || recorded is Mesh)
                return SameStoredMesh(current as Mesh, recorded as Mesh, tolerance) ? null : "的网格未通过一致性校验";

            var liveBrep = current as Brep;
            var savedBrep = recorded as Brep;
            using (var convertedLive = liveBrep == null ? Brep.TryConvertBrep(current) : null)
            using (var convertedSaved = savedBrep == null ? Brep.TryConvertBrep(recorded) : null)
            {
                liveBrep = liveBrep ?? convertedLive;
                savedBrep = savedBrep ?? convertedSaved;
                if (liveBrep == null || savedBrep == null) return "的几何类型无法比对";
                if (liveBrep.IsDuplicate(savedBrep, tolerance)) return null;

                // Compare the actual 3D geometry and used topology, ignoring saved display data.
                using (var live = liveBrep.DuplicateBrep())
                using (var saved = savedBrep.DuplicateBrep())
                {
                    live.Compact();
                    saved.Compact();
                    return SameStoredBrep(live, saved, tolerance) ? null : "的边界或曲面未通过一致性校验";
                }
            }
        }

        private static bool SameStoredMesh(Mesh a, Mesh b, double tolerance)
        {
            if (a == null || b == null || a.Vertices.Count != b.Vertices.Count || a.Faces.Count != b.Faces.Count)
                return false;
            for (int i = 0; i < a.Vertices.Count; i++)
                if (a.Vertices.Point3dAt(i).DistanceTo(b.Vertices.Point3dAt(i)) > tolerance) return false;
            for (int i = 0; i < a.Faces.Count; i++)
            {
                var x = a.Faces[i];
                var y = b.Faces[i];
                if (x.A != y.A || x.B != y.B || x.C != y.C || x.D != y.D) return false;
            }
            return true;
        }

        private static bool SameStoredBrep(Brep a, Brep b, double tolerance)
        {
            if (a.IsSolid != b.IsSolid || a.Vertices.Count != b.Vertices.Count ||
                a.Edges.Count != b.Edges.Count || a.Faces.Count != b.Faces.Count ||
                a.Loops.Count != b.Loops.Count || a.Trims.Count != b.Trims.Count)
                return false;
            for (int i = 0; i < a.Vertices.Count; i++)
                if (a.Vertices[i].Location.DistanceTo(b.Vertices[i].Location) > tolerance) return false;
            for (int i = 0; i < a.Edges.Count; i++)
                if (!SameStoredEdge(a.Edges[i], b.Edges[i], tolerance)) return false;
            for (int i = 0; i < a.Faces.Count; i++)
                if (!SameStoredFace(a.Faces[i], b.Faces[i], tolerance)) return false;
            return true;
        }

        private static bool SameStoredEdge(BrepEdge a, BrepEdge b, double tolerance)
        {
            if (a.StartVertex?.VertexIndex != b.StartVertex?.VertexIndex ||
                a.EndVertex?.VertexIndex != b.EndVertex?.VertexIndex ||
                a.PointAtStart.DistanceTo(b.PointAtStart) > tolerance ||
                a.PointAtEnd.DistanceTo(b.PointAtEnd) > tolerance)
                return false;
            if (GeometryBase.GeometryEquals(a, b)) return true;
            using (var x = a.ToNurbsCurve())
            using (var y = b.ToNurbsCurve())
            {
                if (x != null && y != null && x.EpsilonEquals(y, NurbsParameterTolerance)) return true;
            }
            double max, maxA, maxB, min, minA, minB;
            return Curve.GetDistancesBetweenCurves(a, b, tolerance / 4,
                out max, out maxA, out maxB, out min, out minA, out minB) &&
                max >= 0 && max <= tolerance;
        }

        private static bool SameStoredFace(BrepFace a, BrepFace b, double tolerance)
        {
            if (a.Loops.Count != b.Loops.Count || !SameStoredSurface(a, b, tolerance)) return false;
            for (int i = 0; i < a.Loops.Count; i++)
            {
                var x = a.Loops[i];
                var y = b.Loops[i];
                if (x.LoopType != y.LoopType || x.Trims.Count != y.Trims.Count) return false;
                for (int j = 0; j < x.Trims.Count; j++)
                {
                    var trimA = x.Trims[j];
                    var trimB = y.Trims[j];
                    if (trimA.Edge?.EdgeIndex != trimB.Edge?.EdgeIndex) return false;
                    for (int step = 0; step <= 8; step++)
                    {
                        var uvA = trimA.PointAt(trimA.Domain.ParameterAt(step / 8.0));
                        var uvB = trimB.PointAt(trimB.Domain.ParameterAt(step / 8.0));
                        var pointA = a.PointAt(uvA.X, uvA.Y);
                        var pointB = b.PointAt(uvB.X, uvB.Y);
                        if (!pointA.IsValid || !pointB.IsValid || pointA.DistanceTo(pointB) > tolerance) return false;
                    }
                }
            }
            return true;
        }

        private static bool SameStoredSurface(BrepFace a, BrepFace b, double tolerance)
        {
            Plane planeA, planeB;
            if (a.TryGetPlane(out planeA, tolerance / 4) && b.TryGetPlane(out planeB, tolerance / 4))
                return Vector3d.CrossProduct(planeA.ZAxis, planeB.ZAxis).Length <= 1e-8 &&
                    Math.Abs(planeA.DistanceTo(planeB.Origin)) <= tolerance / 2;

            using (var x = a.UnderlyingSurface().ToNurbsSurface())
            using (var y = b.UnderlyingSurface().ToNurbsSurface())
            {
                if (x == null || y == null || x.Points.CountU != y.Points.CountU ||
                    x.Points.CountV != y.Points.CountV || !x.EpsilonEquals(y, tolerance) ||
                    !x.KnotsU.EpsilonEquals(y.KnotsU, NurbsParameterTolerance) ||
                    !x.KnotsV.EpsilonEquals(y.KnotsV, NurbsParameterTolerance))
                    return false;
                for (int u = 0; u < x.Points.CountU; u++)
                    for (int v = 0; v < x.Points.CountV; v++)
                        if (Math.Abs(x.Points.GetControlPoint(u, v).Weight - y.Points.GetControlPoint(u, v).Weight) >
                            NurbsParameterTolerance) return false;
                return true;
            }
        }
    }
}
