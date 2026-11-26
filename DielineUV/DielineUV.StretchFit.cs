using System;
using System.Collections.Generic;
using System.Linq;

namespace DielineUV
{
    internal sealed class RibbonStations
    {
        private readonly double[][] points;
        private readonly double[] distances;
        internal double Length => distances[distances.Length - 1];
        internal RibbonStations(double[][] points)
        {
            this.points = points;
            distances = new double[points.Length];
            for (int i = 1; i < points.Length; i++)
                distances[i] = distances[i - 1] + Math.Sqrt(Enumerable.Range(0, 3).Sum(k =>
                    Math.Pow(points[i][k] - points[i - 1][k], 2)));
        }
        internal double ClosestLength(double x, double y, double z)
        {
            double best = double.PositiveInfinity, length = 0;
            for (int i = 0; i + 1 < points.Length; i++)
            {
                double dx = points[i + 1][0] - points[i][0], dy = points[i + 1][1] - points[i][1], dz = points[i + 1][2] - points[i][2];
                double squared = dx * dx + dy * dy + dz * dz;
                if (squared <= 1e-20) continue;
                double t = Math.Max(0, Math.Min(1, ((x - points[i][0]) * dx + (y - points[i][1]) * dy + (z - points[i][2]) * dz) / squared));
                double px = x - points[i][0] - t * dx, py = y - points[i][1] - t * dy, pz = z - points[i][2] - t * dz;
                double distance = px * px + py * py + pz * pz;
                if (distance < best) { best = distance; length = distances[i] + t * (distances[i + 1] - distances[i]); }
            }
            return length;
        }
    }

    // Pure 2D geometry, also used by the remote regression checks.
    internal sealed class StretchBox
    {
        internal double X, Y, UX, UY, Width, Height;

        internal static StretchBox Fit(double[][] points)
        {
            if (points == null || points.Length < 3) throw new ArgumentException("轮廓点不足。");
            var sorted = points.OrderBy(p => p[0]).ThenBy(p => p[1]).ToArray();
            var hull = new List<double[]>();
            foreach (var p in sorted)
            {
                while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 1e-12) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            int lower = hull.Count;
            for (int i = sorted.Length - 2; i >= 0; i--)
            {
                var p = sorted[i];
                while (hull.Count > lower && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 1e-12) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            if (hull.Count > 1) hull.RemoveAt(hull.Count - 1);
            StretchBox best = null;
            foreach (int i in Enumerable.Range(0, hull.Count))
            {
                var a = hull[i]; var b = hull[(i + 1) % hull.Count];
                double ux = b[0] - a[0], uy = b[1] - a[1], length = Math.Sqrt(ux * ux + uy * uy);
                if (length < 1e-12) continue;
                ux /= length; uy /= length;
                var xs = hull.Select(p => p[0] * ux + p[1] * uy).ToArray();
                var ys = hull.Select(p => -p[0] * uy + p[1] * ux).ToArray();
                double minx = xs.Min(), maxx = xs.Max(), miny = ys.Min(), maxy = ys.Max();
                double w = maxx - minx, h = maxy - miny;
                if (w < 1e-9 || h < 1e-9 || best != null && w * h >= best.Width * best.Height - 1e-9) continue;
                var candidate = new StretchBox { X = ux * (minx + maxx) / 2 - uy * (miny + maxy) / 2,
                    Y = uy * (minx + maxx) / 2 + ux * (miny + maxy) / 2, UX = ux, UY = uy, Width = w, Height = h };
                if (h > w) { candidate.UX = -uy; candidate.UY = ux; candidate.Width = h; candidate.Height = w; }
                best = candidate;
            }
            if (best == null) throw new ArgumentException("刀模轮廓没有有效的二维范围。");
            return best;
        }

        private static double Cross(double[] a, double[] b, double[] c)
        {
            return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]);
        }

        // Row-major affine matrix [a b tx; c d ty]. Reflection and a quarter turn
        // are offered explicitly so a symmetric dieline never silently fixes orientation.
        internal double[] To(StretchBox target, bool quarterTurn, int signX, int signY)
        {
            double colUX, colUY, colVX, colVY;
            if (!quarterTurn)
            {
                colUX = target.UX * target.Width / Width * signX;
                colUY = target.UY * target.Width / Width * signX;
                colVX = -target.UY * target.Height / Height * signY;
                colVY = target.UX * target.Height / Height * signY;
            }
            else
            {
                colUX = -target.UY * target.Height / Width * signY;
                colUY = target.UX * target.Height / Width * signY;
                colVX = target.UX * target.Width / Height * signX;
                colVY = target.UY * target.Width / Height * signX;
            }
            double a = colUX * UX - colVX * UY, b = colUX * UY + colVX * UX;
            double c = colUY * UX - colVY * UY, d = colUY * UY + colVY * UX;
            return new[] { a, b, target.X - a * X - b * Y, c, d, target.Y - c * X - d * Y };
        }
    }
}
