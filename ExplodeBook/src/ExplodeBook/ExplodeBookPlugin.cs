using ExplodeBook.Core;
using Rhino.PlugIns;

namespace ExplodeBook
{
  public sealed class ExplodeBookPlugin : PlugIn
  {
    public ExplodeBookPlugin()
    {
      Instance = this;
    }

    public static ExplodeBookPlugin Instance { get; private set; }
    internal static ExplodeSettings CurrentSettings { get; } = new ExplodeSettings();
  }
}
