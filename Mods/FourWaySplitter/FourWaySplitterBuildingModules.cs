using System.Collections.Generic;

namespace FourWaySplitter
{
    /// <summary>
    /// HUD side-panel module provider for the FourWaySplitter. v1 has no
    /// side-panel content (no stats, no configuration, no research-gate
    /// hookup) — matches <c>DiagonalCutterBuildingModules</c> which also
    /// yields empty on both overloads. Registered so the game's DI system
    /// has an implementation to resolve; can grow later without churning
    /// the registration surface.
    /// </summary>
    public class FourWaySplitterBuildingModules : IBuildingModules
    {
        public IEnumerable<IHUDSidePanelModuleData> GetInfoModules(IBuildingDefinition definition)
        {
            yield break;
        }

        public IEnumerable<IHUDSidePanelModuleData> GetInfoModules(IMapModel map, BuildingModel building)
        {
            yield break;
        }
    }
}
