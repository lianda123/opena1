using MechanismMaker.Core;
using Rhino.PlugIns;

namespace MechanismMaker
{
  public sealed class MechanismMakerPlugin : PlugIn
  {
    public MechanismMakerPlugin()
    {
      Instance = this;
    }

    public static MechanismMakerPlugin Instance { get; private set; }

    internal static MechanismSettings CurrentSettings { get; } = new MechanismSettings();
  }
}
