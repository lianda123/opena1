using Rhino.PlugIns;

namespace ArcFlow
{
  public sealed class ArcFlowPlugin : PlugIn
  {
    public static ArcFlowPlugin Instance { get; private set; }

    public ArcFlowPlugin()
    {
      Instance = this;
    }
  }
}
