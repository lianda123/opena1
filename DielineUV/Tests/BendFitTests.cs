using System;
using System.Linq;
using DielineUV;

internal static class BendFitTests
{
    private static int checks;
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static double[][] Rectangle(double x, double y, double width, double height, double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return new[] { new[] { -.5, -.5 }, new[] { .5, -.5 }, new[] { .5, .5 }, new[] { -.5, .5 } }
            .Select(p => new[] { x + c * p[0] * width - s * p[1] * height, y + s * p[0] * width + c * p[1] * height }).ToArray();
    }
    internal static int Run()
    {
        for (int i = 0; i < 40; i++)
        {
            var original = StretchBox.Fit(Rectangle(0, 0, 41.7237727339, 20.132076146, i * .079));
            var target = StretchBox.Fit(Rectangle(1600, 45, 40.0416224935, 20.132076146, i * -.13));
            foreach (bool swap in new[] { false, true })
                foreach (int sx in new[] { 1, -1 })
                    foreach (int sy in new[] { 1, -1 })
                    {
                        var m = original.To(target, swap, sx, sy);
                        foreach (var p in Rectangle(original.X, original.Y, original.Width, original.Height, Math.Atan2(original.UY, original.UX)))
                        {
                            double x = m[0] * p[0] + m[1] * p[1] + m[2] - target.X;
                            double y = m[3] * p[0] + m[4] * p[1] + m[5] - target.Y;
                            Check(Math.Abs(Math.Abs(x * target.UX + y * target.UY) - target.Width / 2) < 1e-7, "wrong fitted width");
                            Check(Math.Abs(Math.Abs(-x * target.UY + y * target.UX) - target.Height / 2) < 1e-7, "wrong fitted height");
                        }
                    }
        }
        // Dimensions from the supplied curved-panel file, without uploading the model.
        const double radius = 23.80383223647356, angle = 1.6821502554973622;
        var middle = Enumerable.Range(0, 2401).Select(i => new[] { radius * Math.Cos(angle * i / 2400), radius * Math.Sin(angle * i / 2400), 0.0 }).ToArray();
        var station = new RibbonStations(middle);
        Check(Math.Abs(station.Length - 40.04162247840035) < .00001, "middle-surface length changed");
        var reversed = new RibbonStations(middle.Reverse().ToArray());
        for (int i = 0; i <= 30; i++)
        {
            double fraction = i / 30.0, a = angle * fraction;
            foreach (double r in new[] { radius - 1, radius, radius + 1 })
            {
                double x = r * Math.Cos(a), y = r * Math.Sin(a);
                double value = station.ClosestLength(x, y, 0);
                Check(Math.Abs(value - radius * a) < .002, "inner/outer bend station diverged");
                Check(Math.Abs(value + reversed.ClosestLength(x, y, 0) - station.Length) < .002, "reversed station mismatch");
            }
        }
        // S-shaped strip: both offset rails share the same center stations.
        var sCurve = Enumerable.Range(0, 2001).Select(i => new[] { 10 * Math.Sin(2 * Math.PI * i / 2000), 60.0 * i / 2000, 0.0 }).ToArray();
        var sStations = new RibbonStations(sCurve);
        double last = -1;
        for (int i = 0; i <= 2000; i += 25)
        {
            double value = sStations.ClosestLength(sCurve[i][0], sCurve[i][1], 0);
            Check(value > last, "S strip station runs backward");
            last = value;
        }
        Console.WriteLine("Neutral bend regression checks passed: " + checks);
        return checks;
    }
}
