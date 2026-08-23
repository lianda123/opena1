using System;
using System.Collections.Generic;
using Rhino.Geometry;

namespace WoodExport.Core
{
  internal static class StrokeFont
  {
    private struct Segment
    {
      public Segment(double x1, double y1, double x2, double y2)
      {
        X1 = x1;
        Y1 = y1;
        X2 = x2;
        Y2 = y2;
      }

      public double X1;
      public double Y1;
      public double X2;
      public double Y2;
    }

    private static readonly Dictionary<char, string> SevenSegment =
      new Dictionary<char, string>
      {
        { '0', "AB CDEF".Replace(" ", string.Empty) },
        { '1', "BC" },
        { '2', "ABGED" },
        { '3', "ABGCD" },
        { '4', "FGBC" },
        { '5', "AFGCD" },
        { '6', "AFGECD" },
        { '7', "ABC" },
        { '8', "ABCDEFG" },
        { '9', "AFGBCD" }
      };

    private static readonly Dictionary<char, Segment> SegmentMap =
      new Dictionary<char, Segment>
      {
        { 'A', new Segment(0.0, 1.0, 0.62, 1.0) },
        { 'B', new Segment(0.62, 1.0, 0.62, 0.52) },
        { 'C', new Segment(0.62, 0.48, 0.62, 0.0) },
        { 'D', new Segment(0.0, 0.0, 0.62, 0.0) },
        { 'E', new Segment(0.0, 0.0, 0.0, 0.48) },
        { 'F', new Segment(0.0, 0.52, 0.0, 1.0) },
        { 'G', new Segment(0.0, 0.5, 0.62, 0.5) }
      };

    public static double MeasureWidth(string text, double height)
    {
      if (string.IsNullOrEmpty(text) || height <= 0.0)
        return 0.0;
      var width = 0.0;
      foreach (var character in text)
        width += GlyphAdvance(character) * height;
      return Math.Max(0.0, width - 0.14 * height);
    }

    public static List<Curve> Create(string text, double height, Point3d lowerLeft)
    {
      var result = new List<Curve>();
      if (string.IsNullOrEmpty(text) || height <= 0.0)
        return result;
      var cursor = lowerLeft.X;
      foreach (var sourceCharacter in text.ToUpperInvariant())
      {
        var character = sourceCharacter;
        string segmentNames;
        if (SevenSegment.TryGetValue(character, out segmentNames))
        {
          foreach (var segmentName in segmentNames)
            Add(result, SegmentMap[segmentName], cursor, lowerLeft.Y, height);
        }
        else if (character == 'P')
        {
          Add(result, new Segment(0.0, 0.0, 0.0, 1.0), cursor, lowerLeft.Y, height);
          Add(result, new Segment(0.0, 1.0, 0.62, 1.0), cursor, lowerLeft.Y, height);
          Add(result, new Segment(0.62, 1.0, 0.62, 0.5), cursor, lowerLeft.Y, height);
          Add(result, new Segment(0.0, 0.5, 0.62, 0.5), cursor, lowerLeft.Y, height);
        }
        else if (character == '-')
        {
          Add(result, new Segment(0.0, 0.5, 0.48, 0.5), cursor, lowerLeft.Y, height);
        }
        else if (character == '.')
        {
          Add(result, new Segment(0.08, 0.0, 0.08, 0.06), cursor, lowerLeft.Y, height);
        }
        cursor += GlyphAdvance(character) * height;
      }
      return result;
    }

    private static double GlyphAdvance(char character)
    {
      if (character == '.') return 0.25;
      if (character == '-') return 0.62;
      return 0.76;
    }

    private static void Add(
      ICollection<Curve> destination,
      Segment segment,
      double originX,
      double originY,
      double height)
    {
      var start = new Point3d(originX + segment.X1 * height, originY + segment.Y1 * height, 0.0);
      var end = new Point3d(originX + segment.X2 * height, originY + segment.Y2 * height, 0.0);
      if (start.DistanceToSquared(end) <= 1e-16)
        return;
      destination.Add(new LineCurve(start, end));
    }
  }
}
