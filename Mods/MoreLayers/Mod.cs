using Game.Core.Modding;
using JetBrains.Annotations;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace MoreLayers
{
    /// <summary>
    /// Cap-raising Shapez 2 mod. Once the player reaches the vanilla
    /// Milestone_PlatformLayer3 unlock, this mod lifts the platform-layer
    /// cap from 3 to 6 — every belt and building that works on layers 1-3
    /// becomes usable on layers 4, 5, 6 with no new milestones.
    ///
    /// Mechanism: registers <see cref="MoreLayersScenarioRewirer"/> as an
    /// <c>IGameScenarioRewirer</c>; on every scenario load it appends three
    /// duplicate entries (pointing to the layer-3 milestone) to
    /// <c>GameScenario.Mechanics.BuildingLayerUnlocks</c>. See
    /// <c>.oes/cap-discovery.md</c> for the full design rationale.
    ///
    /// One known outlier (NotchConnectorsExtender's hardcoded literal 3 loop)
    /// is patched separately; see <see cref="MoreLayersScenarioRewirer"/>'s
    /// sibling work in P02 Task 3.
    /// </summary>
    [UsedImplicitly]
    public class MoreLayersMod : IMod
    {
        private RewirerHandle _scenarioRewirerHandle;
        private MoreLayersDrawDataHook _drawDataHook;
        private MoreLayersBoundsHook _boundsHook;

        public MoreLayersMod(ILogger logger)
        {
            _scenarioRewirerHandle = GameRewirers.AddRewirer(new MoreLayersScenarioRewirer());
            logger.Info?.Log("MoreLayers: registered scenario rewirer (cap raise 3 → 6 on Milestone_PlatformLayer3 unlock).");

            // Per UAT-P02 + Player.log: BuildingDrawDataFactory.FromMeta hardcodes
            // MainMeshPerLayer to size 3; layer-4+ placement throws IOORE in
            // StaticBuildingMeshBuilder.BuildBaseMesh, aborting the chunk's mesh
            // build → static meshes vanish. The hook extends MainMeshPerLayer to
            // size 7 AND extends ThemeResources.BeltCap*/PipeStands*/WireCap*
            // arrays from size 3 to size 7 (UAT-P02 re-test 3 finding).
            _drawDataHook = new MoreLayersDrawDataHook(logger);

            // Per UAT-P02 re-test 3: close-camera frustum-vs-AABB cull tests fail
            // because IslandChunkStaticBuildingsDrawer.ContentBounds and
            // BuildingSimpleAnimationDrawer.ChunkCullingDimensions cache Z=4
            // bounds. Patches both to Z=7.
            _boundsHook = new MoreLayersBoundsHook(logger);
        }

        public void Dispose()
        {
            _boundsHook?.Dispose();
            _drawDataHook?.Dispose();
            GameRewirers.RemoveRewirer(_scenarioRewirerHandle);
        }
    }
}
