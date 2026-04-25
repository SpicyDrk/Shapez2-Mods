using Game.Core.Research;
using ShapezShifter.Hijack;

namespace MoreLayers
{
    /// <summary>
    /// Raises the platform-layer cap from the vanilla 3 to <see cref="TargetCap"/>
    /// by appending duplicate entries to <c>GameScenario.Mechanics.BuildingLayerUnlocks</c>.
    ///
    /// The cap is data-driven: <c>GameMode.MaxBuildingLayer</c> returns
    /// <c>Mechanics.BuildingLayerUnlocks.Count</c>, and
    /// <c>BaseMapInteractionMode.GetMaximumAllowedBuildingLayer</c> walks the list
    /// backwards returning the index+1 of the first unlocked entry. By duplicating
    /// the existing last entry (which IS the layer-3 milestone) into indices 3, 4, 5,
    /// the moment the player unlocks that one milestone all four entries report
    /// <c>IsUnlocked == true</c> and the function returns 6.
    ///
    /// Below the layer-3 milestone the behavior is bit-identical to vanilla:
    /// the walk falls through to indices 2, 1, 0, returning whatever vanilla would
    /// have returned. See <c>.oes/cap-discovery.md</c> for the full analysis.
    /// </summary>
    internal sealed class MoreLayersScenarioRewirer : IGameScenarioRewirer
    {
        private const int TargetCap = 6;

        public GameScenario ModifyGameScenario(GameScenario gameScenario)
        {
            var unlocks = gameScenario.Mechanics.BuildingLayerUnlocks;

            // Defensive: empty list (shouldn't happen in shipped scenarios) means
            // we have nothing to duplicate — bail rather than guess.
            if (unlocks.Count == 0)
            {
                return gameScenario;
            }

            // Idempotent: GameScenarioInterceptor.Postfix can fire multiple times
            // across a session (e.g., scenario reloads). Don't keep growing the list.
            if (unlocks.Count >= TargetCap)
            {
                return gameScenario;
            }

            // The last entry corresponds to the layer-3 milestone (index 2 → layer 3
            // by the index+1 convention). Duplicating it into the new slots means
            // unlocking layer 3 also unlocks layers 4, 5, 6 in the same observer pass.
            var lastUnlock = unlocks[unlocks.Count - 1];
            while (unlocks.Count < TargetCap)
            {
                unlocks.Add(lastUnlock);
            }

            return gameScenario;
        }

        public bool Equals(IRewirer other) => ReferenceEquals(this, other);
    }
}
