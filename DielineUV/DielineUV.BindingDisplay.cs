using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;

namespace DielineUV
{
    public sealed partial class DielineUVCommand
    {
        private sealed class BindingDisplay : DisplayConduit, IDisposable
        {
            private static readonly Color BoundColor = Color.FromArgb(20, 170, 91);
            private readonly RhinoDoc doc;
            private readonly List<ProjectPart> parts;
            private readonly HashSet<Guid> bound = new HashSet<Guid>();
            private readonly HashSet<Guid> hidden = new HashSet<Guid>();

            public BindingDisplay(RhinoDoc doc, List<ProjectPart> parts)
            {
                this.doc = doc;
                this.parts = parts;
            }

            public bool HideLayout { get; private set; } = true;
            public bool Reviewing { get; private set; }
            public string Description => Reviewing ? "正在查看已绑" : HideLayout ? "已绑刀模隐藏" : "已绑刀模标绿";

            public bool IsBound(Guid id)
            {
                return bound.Contains(id);
            }

            public void Add(ProjectPart part)
            {
                parts.Add(part);
                bound.Add(part.SourceId);
                bound.UnionWith(part.LayoutIds);
                UpdateVisibility(part);
                Redraw();
            }

            public void ToggleLayout()
            {
                HideLayout = !HideLayout;
                RefreshVisibility();
            }

            public void ToggleReview()
            {
                Reviewing = !Reviewing;
                RefreshVisibility();
            }

            public void UndoLast()
            {
                var part = parts[parts.Count - 1];
                parts.RemoveAt(parts.Count - 1);
                bound.Remove(part.SourceId);
                bound.ExceptWith(part.LayoutIds);
                Restore(part.SourceId);
                foreach (var id in part.LayoutIds) Restore(id);
                foreach (var guide in part.Guides) guide.Dispose();
                if (parts.Count == 0) Reviewing = false;
                RhinoApp.WriteLine("已撤销「{0}」的绑定，可重新选择原件和刀模。", part.Label);
                Redraw();
            }

            private void RefreshVisibility()
            {
                foreach (var part in parts) UpdateVisibility(part);
                Redraw();
            }

            private void UpdateVisibility(ProjectPart part)
            {
                SetHidden(part.SourceId, !Reviewing);
                foreach (var id in part.LayoutIds) SetHidden(id, !Reviewing && HideLayout);
            }

            private void SetHidden(Guid id, bool shouldHide)
            {
                if (!shouldHide)
                {
                    Restore(id);
                    return;
                }
                if (hidden.Contains(id)) return;
                var obj = doc.Objects.FindId(id);
                if (obj != null && !obj.IsReference && obj.Attributes.Mode == ObjectMode.Normal &&
                    doc.Objects.Hide(id, false))
                    hidden.Add(id);
            }

            private void Restore(Guid id)
            {
                if (!hidden.Contains(id)) return;
                var obj = doc.Objects.FindId(id);
                if (obj == null || obj.Attributes.Mode != ObjectMode.Hidden || doc.Objects.Show(id, true))
                    hidden.Remove(id);
            }

            private void Redraw()
            {
                Enabled = parts.Count != 0;
                doc.Views.Redraw();
            }

            protected override void DrawForeground(DrawEventArgs e)
            {
                if (e.RhinoDoc?.RuntimeSerialNumber != doc.RuntimeSerialNumber) return;
                for (int i = 0; i < parts.Count; i++)
                {
                    var part = parts[i];
                    string number = (i + 1).ToString(CultureInfo.InvariantCulture);
                    // Also mark any selected reference curve Rhino could not hide.
                    if (Reviewing || !HideLayout || part.LayoutIds.Any(id => !hidden.Contains(id)))
                    {
                        foreach (var guide in part.Guides) e.Display.DrawCurve(guide, BoundColor, 2);
                        e.Display.DrawDot(part.LayoutMarker, number, BoundColor, Color.White);
                    }
                    if (Reviewing || !hidden.Contains(part.SourceId))
                        e.Display.DrawDot(part.SourceMarker, number, BoundColor, Color.White);
                }
            }

            public void Dispose()
            {
                Enabled = false;
                foreach (var id in hidden.ToArray()) Restore(id);
                if (hidden.Count != 0)
                    RhinoApp.WriteLine("有 {0} 个本次隐藏的对象未能恢复显示。", hidden.Count);
                doc.Views.Redraw();
            }
        }
    }
}
