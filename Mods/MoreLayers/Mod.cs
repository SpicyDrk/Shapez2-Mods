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
    /// Two hooks compose the fix:
    /// - <see cref="MoreLayersScenarioRewirer"/>: appends 3 duplicate entries
    ///   to <c>GameScenario.Mechanics.BuildingLayerUnlocks</c> so the
    ///   layer-3 milestone unlock raises <c>MaxBuildingLayer</c> to 6.
    /// - <see cref="MoreLayersDrawDataHook"/>: postfix on
    ///   <c>BuildingDrawDataFactory.FromMeta</c>; extends per-building
    ///   <c>MainMeshPerLayer</c> from size 3 to size 7 AND extends every
    ///   size-3 <c>LODMeshAsset[]</c> on the shared
    ///   <c>VisualThemeBaseResources</c> (BeltCap*/PipeStands*/WireCap*).
    ///
    /// Plan-007 diagnostic logging (Player.log evidence) showed that:
    /// 1. The cap-raise rewirer propagates correctly — every drawer
    ///    constructor receives <c>maxBuildingLayer=6</c>; bounds-cache
    ///    patches are unnecessary or actively harmful and were removed.
    /// 2. The remaining per-frame <c>IndexOutOfRangeException</c> traced
    ///    to <c>MapSoundManager.UpdateClusterEntities</c> indexing
    ///    <c>MapSoundSettings.ScoreByHeight[BuildingLayer()]</c> where
    ///    that array is hardcoded <c>new float[3]</c>. See
    ///    <see cref="MoreLayersAudioHook"/>.
    /// </summary>
    [UsedImplicitly]
    public class MoreLayersMod : IMod
    {
        private RewirerHandle _scenarioRewirerHandle;
        private MoreLayersDrawDataHook _drawDataHook;
        private MoreLayersAudioHook _audioHook;

        public MoreLayersMod(ILogger logger)
        {
            _scenarioRewirerHandle = GameRewirers.AddRewirer(new MoreLayersScenarioRewirer());
            logger.Info?.Log("MoreLayers: registered scenario rewirer (cap raise 3 → 6 on Milestone_PlatformLayer3 unlock).");

            _drawDataHook = new MoreLayersDrawDataHook(logger);
            _audioHook = new MoreLayersAudioHook(logger);
        }

        public void Dispose()
        {
            _audioHook?.Dispose();
            _drawDataHook?.Dispose();
            GameRewirers.RemoveRewirer(_scenarioRewirerHandle);
        }
    }
}
