using System;
using System.Runtime.InteropServices;
using ProductMotionTimeline.Core;
using ProductMotionTimeline.UI;
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
      Panels.RegisterPanel(this, typeof(TimelinePanel), "产品动态时间轴", null);
      _mechanicalConduit = new MechanicalConstraintConduit { Enabled = true };
      return LoadReturnCode.Success;
    }

    protected override void OnShutdown()
    {
      TimelineRepository.Shutdown();
      if (_mechanicalConduit != null)
        _mechanicalConduit.Enabled = false;
      base.OnShutdown();
    }
  }
}
