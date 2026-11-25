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
            public bool Reviewing { get; private set; }

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
                if (!Reviewing) Hide(part.BoundSourceId);
                if (HideLayout && !Reviewing)
                    foreach (var id in part.LayoutIds) Hide(id);
                Enabled = true;
                doc.Views.Redraw();
            }

            public void ToggleLayout()
            {
                HideLayout = !HideLayout;
                foreach (var part in parts)
                    foreach (var id in part.LayoutIds)
                        if (HideLayout && !Reviewing) Hide(id);
                        else Show(id);
                doc.Views.Redraw();
            }

            public void ToggleReview()
            {
                Reviewing = !Reviewing;
                foreach (var part in parts)
                {
                    if (Reviewing) Show(part.BoundSourceId);
                    else Hide(part.BoundSourceId);
                    foreach (var id in part.LayoutIds)
                        if (Reviewing || !HideLayout) Show(id);
                        else Hide(id);
                }
                Enabled = true;
                doc.Views.Redraw();
            }

            public void UndoLast()
            {
                if (parts.Count == 0) { RhinoApp.WriteLine("当前没有可撤回的绑定。"); return; }
                var part = parts[parts.Count - 1];
                parts.RemoveAt(parts.Count - 1);
                bound.Remove(part.BoundSourceId);
                bound.ExceptWith(part.LayoutIds);
                Show(part.BoundSourceId);
                foreach (var id in part.LayoutIds) Show(id);
                foreach (var curve in part.Guides) curve.Dispose();
                if (parts.Count == 0) Reviewing = false;
                RhinoApp.WriteLine("已撤回「{0}」的绑定，已恢复原件和刀模，可重新选择。现有 {1} 件。", part.Label, parts.Count);
                doc.Views.Redraw();
            }

            public void Resume()
            {
                foreach (var part in parts) Mark(part);
                Enabled = true;
                doc.Views.Redraw();
            }

            public void RestoreVisibility()
            {
                Enabled = false;
                foreach (var id in hidden.ToArray()) Show(id);
                foreach (var part in parts)
                    part.Source = doc.Objects.FindId(part.BoundSourceId);
                doc.Views.Redraw();
                if (hidden.Count != 0)
                    throw new InvalidOperationException("有本次隐藏的对象无法恢复显示，请使用 Show 恢复。");
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
                if (e.RhinoDoc?.RuntimeSerialNumber != doc.RuntimeSerialNumber) return;
                for (int i = 0; i < parts.Count; i++)
                {
                    var bounds = BoundingBox.Empty;
                    foreach (var curve in parts[i].Guides)
                    {
                        if (!HideLayout || Reviewing) e.Display.DrawCurve(curve, Green, 2);
                        bounds.Union(curve.GetBoundingBox(true));
                    }
                    if (bounds.IsValid && (!HideLayout || Reviewing))
                        e.Display.DrawDot(bounds.Center, (i + 1).ToString(CultureInfo.InvariantCulture), Green, Color.White);
                    if (Reviewing)
                    {
                        var source = doc.Objects.FindId(parts[i].BoundSourceId);
                        if (source != null) e.Display.DrawDot(source.Geometry.GetBoundingBox(true).Center,
                            (i + 1).ToString(CultureInfo.InvariantCulture), Green, Color.White);
                    }
                }
            }

            public void Dispose()
            {
                RestoreVisibility();
            }
        }
    }
}
