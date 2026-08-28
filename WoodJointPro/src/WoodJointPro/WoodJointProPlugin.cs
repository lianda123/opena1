using Rhino.PlugIns;

namespace WoodJointPro
{
  public sealed class WoodJointProPlugin : PlugIn
  {
    public WoodJointProPlugin()
    {
      Instance = this;
    }

    public static WoodJointProPlugin Instance { get; private set; }
  }
}
