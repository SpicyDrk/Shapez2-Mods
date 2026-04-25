using Game.Core.Modding;
using JetBrains.Annotations;
using ILogger = Core.Logging.ILogger;

namespace MoreLayers
{
    /// <summary>
    /// Cap-raising Shapez 2 mod. Once the player reaches the vanilla
    /// Milestone_PlatformLayer3 unlock, this mod lifts the platform-layer
    /// cap from 3 to 6 — every belt and building that works on layers 1-3
    /// becomes usable on layers 4, 5, 6 with no new milestones.
    ///
    /// P01 leaves this as a no-op skeleton: the constructor exists only to
    /// satisfy IMod / Shifter's DI convention. The actual cap patch lands
    /// in P02 once the cap-discovery spike (.oes/cap-discovery.md)
    /// identifies the enforcement sites.
    /// </summary>
    [UsedImplicitly]
    public class MoreLayersMod : IMod
    {
        public MoreLayersMod(ILogger logger)
        {
            // Intentionally empty in P01. Cap patch wires in here in P02.
        }

        public void Dispose()
        {
        }
    }
}
