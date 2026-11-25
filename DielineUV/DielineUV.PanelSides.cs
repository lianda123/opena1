using System;
using System.Linq;

namespace DielineUV
{
    // Face normals and Brep orientation flags are deliberately not inputs.
    // A panel's two sides are separated by their position across its thickness.
    internal static class PanelSidePolicy
    {
        internal static bool Split(double[] positions, double sign, double tolerance, out int[] front, out int[] back)
        {
            front = back = new int[0];
            if (positions == null || positions.Length < 2 || positions.Any(x => double.IsNaN(x) || double.IsInfinity(x))) return false;
            double min = positions.Min(), max = positions.Max();
            if (max - min <= tolerance * 2) return false;
            double middle = min + (max - min) / 2;
            front = Enumerable.Range(0, positions.Length).Where(i => (positions[i] - middle) * sign > 0).ToArray();
            back = Enumerable.Range(0, positions.Length).Except(front).ToArray();
            return front.Length > 0 && back.Length > 0;
        }
    }
}
