using System;
using System.Linq;
using System.Runtime.InteropServices;
using ProductMotionTimeline.Core;
using ProductMotionTimeline.UI;
using Rhino;
using Rhino.DocObjects;
using Rhino.PlugIns;
using Rhino.UI;

namespace ProductMotionTimeline
{
  [Guid("F9A7EFD6-7BBE-4E9D-A7C6-4BBE9B7DE101")]
  public sealed class ProductMotionPlugin : PlugIn
  {
    public static ProductMotionPlugin Instance { get; private set; }
    private MechanicalConstraintConduit _mechanicalConduit;

    public ProductMotionPlugin()
    {
      Instance = this;
    }

    protected override LoadReturnCode OnLoad(ref string errorMessage)
    {
      TimelineRepository.Initialize();
      TimelineDocking.Initialize(this);
      _mechanicalConduit = new MechanicalConstraintConduit { Enabled = true };
      RhinoDoc.SelectObjects += OnRhinoObjectsSelected;
      return LoadReturnCode.Success;
    }

    protected override void OnShutdown()
    {
      TimelineRepository.Shutdown();
      RhinoDoc.SelectObjects -= OnRhinoObjectsSelected;
      if (_mechanicalConduit != null)
        _mechanicalConduit.Enabled = false;
      base.OnShutdown();
    }

    private static void OnRhinoObjectsSelected(object sender, RhinoObjectSelectionEventArgs e)
    {
      if (e == null || !e.Selected || TimelineEngine.SynchronizingRhinoSelection)
        return;
      var doc = e.Document ?? RhinoDoc.ActiveDoc;
      var selected = e.RhinoObjects?
        .OfType<InstanceObject>()
        .LastOrDefault();
      if (doc != null && selected != null)
        TimelineEngine.SelectTrackFromRhinoObject(doc, selected);
    }
  }
}
