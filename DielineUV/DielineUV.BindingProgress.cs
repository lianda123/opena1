using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace DielineUV
{
    public sealed partial class DielineUVCommand
    {
        private sealed class BindingProgress : DisplayConduit, IDisposable
        {
            private readonly RhinoDoc doc;
            private readonly List<ProjectPart> parts;
            private readonly HashSet<Guid> bound = new HashSet<Guid>();
            private readonly HashSet<Guid> hidden = new HashSet<Guid>();
            private static readonly Color Green = Color.FromArgb(20, 170, 91);

            public bool HideLayout { get; private set; } = true;

            public BindingProgress(RhinoDoc doc, List<ProjectPart> parts)
            {
                this.doc = doc;
                this.parts = parts;
            }

            public bool IsBound(Guid id) => bound.Contains(id);

            public void Mark(ProjectPart part)
            {
                bound.Add(part.BoundSourceId);
                bound.UnionWith(part.LayoutIds);
                Hide(part.BoundSourceId);
                if (HideLayout)
                    foreach (var id in part.LayoutIds) Hide(id);
                Enabled = true;
                doc.Views.Redraw();
            }

            public void ToggleLayout()
            {
                HideLayout = !HideLayout;
                foreach (var part in parts)
                    foreach (var id in part.LayoutIds)
                        if (HideLayout) Hide(id);
                        else Show(id);
                doc.Views.Redraw();
            }

            private void Hide(Guid id)
            {
                if (hidden.Contains(id)) return;
                var obj = doc.Objects.FindId(id);
                if (obj != null && obj.Attributes.Mode == ObjectMode.Normal && !obj.IsReference &&
                    doc.Objects.Hide(id, false))
                    hidden.Add(id);
            }

            private void Show(Guid id)
            {
                if (!hidden.Contains(id)) return;
                var obj = doc.Objects.FindId(id);
                if (obj == null || obj.Attributes.Mode != ObjectMode.Hidden || doc.Objects.Show(id, true))
                    hidden.Remove(id);
            }

            protected override void DrawForeground(DrawEventArgs e)
            {
                if (HideLayout || e.RhinoDoc?.RuntimeSerialNumber != doc.RuntimeSerialNumber) return;
                for (int i = 0; i < parts.Count; i++)
                {
                    var bounds = BoundingBox.Empty;
                    foreach (var curve in parts[i].Guides)
                    {
                        e.Display.DrawCurve(curve, Green, 2);
                        bounds.Union(curve.GetBoundingBox(true));
                    }
                    if (bounds.IsValid)
                        e.Display.DrawDot(bounds.Center, (i + 1).ToString(CultureInfo.InvariantCulture), Green, Color.White);
                }
            }

            public void Dispose()
            {
                Enabled = false;
                foreach (var id in hidden.ToArray()) Show(id);
                foreach (var part in parts)
                    part.Source = doc.Objects.FindId(part.BoundSourceId);
                doc.Views.Redraw();
                if (hidden.Count != 0)
                    throw new InvalidOperationException("有本次隐藏的对象无法恢复显示，请使用 Show 恢复。");
            }
        }
    }
}
