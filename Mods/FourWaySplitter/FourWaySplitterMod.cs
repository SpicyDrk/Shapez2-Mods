using Game.Core.Modding;
using JetBrains.Annotations;
using ILogger = Core.Logging.ILogger;

namespace FourWaySplitter
{
    /// <summary>
    /// FourWaySplitter — a 1x1 / 2-level Shapez 2 building that splits the
    /// four quadrants of an incoming shape to four cardinal outputs on the
    /// upper platform level.
    ///
    /// Phase P01: empty stub. The real building registration (connectors,
    /// simulation, renderer, toolbar placement) arrives in P02. For now
    /// Shifter just needs to recognize this mod's assembly + manifest so
    /// that the name appears in the loaded-mods list.
    /// </summary>
    [UsedImplicitly]
    public class FourWaySplitterMod : IMod
    {
        public FourWaySplitterMod(ILogger logger)
        {
        }

        public void Dispose()
        {
        }
    }
}
