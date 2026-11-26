using System;
using System.Linq;
using DielineUV;

internal static class Program
{
    private static int assertions;
    private static void Require(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }

    private static void Check(string label, double[] heights, int[] upper)
    {
        foreach (double sign in new[] { 1.0, -1.0 })
        {
            int[] front, back;
            Require(PanelSidePolicy.Split(heights, sign, .001, out front, out back), label + " cannot split");
            var expected = sign > 0 ? upper : Enumerable.Range(0, heights.Length).Except(upper).ToArray();
            Require(front.SequenceEqual(expected), label + " wrong print side");
            Require(!front.Intersect(back).Any(), label + " overlaps sides");
            Require(front.Concat(back).Distinct().Count() == heights.Length, label + " lost a face");
        }
    }

    public static void Main()
    {
        // Numeric plane positions read from the supplied 111.3dm. No model data is uploaded.
        Check("first panel", new[] { 58.25802517946867, 60.25802517946867 }, new[] { 1 });
        Check("opposite surface orientation", new[] { 62.25802517947306, 60.25802517947306 }, new[] { 0 });
        Check("mirrored copy", new[] { 62.25802517947306, 60.25802517947306 }, new[] { 0 });
        Check("recessed faces", new[] { 60.268025179, 60.268025179, 60.268025179, 60.268025179, 60.258025179, 62.258025179 }, new[] { 5 });
        Check("many coplanar faces", new[] { 0.0, 0.0, 0.01, 2.0, 2.0 }, new[] { 3, 4 });
        for (int i = 0; i < 100; i++)
            Check("translated plane " + i, new[] { i * 37.1, i * 37.1 + 2 }, new[] { 1 });
        int[] a, b;
        Require(!PanelSidePolicy.Split(new[] { 1.0, 1.0 }, 1, .001, out a, out b), "zero thickness accepted");
        Require(!PanelSidePolicy.Split(new[] { double.NaN, 2.0 }, 1, .001, out a, out b), "invalid plane accepted");
        Console.WriteLine("Panel side regression checks passed: " + assertions);
        BendFitTests.Run();
        TransparentPixelTests.Run();
    }
}
