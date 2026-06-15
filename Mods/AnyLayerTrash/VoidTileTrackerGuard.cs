using System;
using System.Reflection;
using Game.Core.Rendering.Islands.PlayingField;
using MonoMod.RuntimeDetour;
using ILogger = Core.Logging.ILogger;

namespace AnyLayerTrash
{
    /// <summary>
    /// Makes the render-only <c>MapPlayingfieldVoidTileTracker</c> tolerant of trash
    /// stacked across layers. That tracker keys its cache by island and only records
    /// the <c>z==0</c> tile of each "render-void-below" building (vanilla trash). It
    /// assumes at most one such building per (x,y) column. This mod stacks trash on
    /// layers 0/1/2, so deleting a column unregisters several trash from one column:
    /// removing the z==0 one empties the island's set and drops the island entry,
    /// then the z=1/z=2 unregisters throw <c>Entry not found in cache</c>. That throw
    /// is re-raised as an <c>AggregateException</c> and propagates out of
    /// <c>ActionModifyIsland.ExecuteDelete</c>, ABORTING the surrounding action —
    /// which is why a platform-blueprint paste (delete-then-recreate the island)
    /// wiped the platform's buildings instead of replacing them.
    ///
    /// The tracker only drives playing-field "void below" visuals, so swallowing
    /// these bookkeeping throws is safe: after a full-column delete the island entry
    /// ends up correctly removed regardless of order, and only the redundant z!=0
    /// unregisters (whose entry is already gone) are suppressed. See CODE-NOTES.md.
    /// </summary>
    internal sealed class VoidTileTrackerGuard : IDisposable
    {
        private readonly ILogger _logger;
        private Hook? _hookRegister;
        private Hook? _hookUnregister;

        private static VoidTileTrackerGuard? _active;

        public VoidTileTrackerGuard(ILogger logger)
        {
            _logger = logger;
        }

        // Trampoline signature for the instance methods (self first).
        private delegate void TrackOrig(MapPlayingfieldVoidTileTracker self, BuildingModel building);

        public void Install()
        {
            _active = this;

            _hookRegister = TryHook(
                nameof(MapPlayingfieldVoidTileTracker.RegisterBuilding),
                nameof(RegisterDetour));
            _hookUnregister = TryHook(
                nameof(MapPlayingfieldVoidTileTracker.UnregisterBuilding),
                nameof(UnregisterDetour));

            if (_hookRegister != null || _hookUnregister != null)
            {
                _logger.Info?.Log("[AnyLayerTrash:voidtiles] guard installed — void-tile tracker made layer-stack tolerant.");
            }
        }

        private Hook? TryHook(string targetName, string detourName)
        {
            MethodInfo? target = typeof(MapPlayingfieldVoidTileTracker).GetMethod(
                targetName, BindingFlags.Instance | BindingFlags.Public);
            if (target == null)
            {
                _logger.Warning?.Log($"[AnyLayerTrash:voidtiles] {targetName} not found — guard NOT installed for it.");
                return null;
            }
            MethodInfo detour = typeof(VoidTileTrackerGuard).GetMethod(
                detourName, BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"{detourName} not found.");
            try
            {
                return new Hook(target, detour);
            }
            catch (Exception ex)
            {
                // Hooking force-JITs the target; if that fails, degrade gracefully
                // rather than taking the whole mod down. See CODE-NOTES.md.
                _logger.Warning?.Log($"[AnyLayerTrash:voidtiles] failed to hook {targetName} ({ex.Message}) — guard NOT installed for it.");
                return null;
            }
        }

        private static void RegisterDetour(TrackOrig orig, MapPlayingfieldVoidTileTracker self, BuildingModel building)
        {
            try
            {
                orig(self, building);
            }
            catch (Exception ex)
            {
                // Duplicate void tile across a stacked column — cosmetic; ignore.
                _active?._logger.Debug?.Log($"[AnyLayerTrash:voidtiles] register swallowed: {ex.Message}");
            }
        }

        private static void UnregisterDetour(TrackOrig orig, MapPlayingfieldVoidTileTracker self, BuildingModel building)
        {
            try
            {
                orig(self, building);
            }
            catch (Exception ex)
            {
                // Island entry/tile already gone (sibling layer removed it first);
                // suppress so the throw can't abort the surrounding delete action.
                _active?._logger.Debug?.Log($"[AnyLayerTrash:voidtiles] unregister swallowed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _hookUnregister?.Dispose();
            _hookUnregister = null;
            _hookRegister?.Dispose();
            _hookRegister = null;
            if (ReferenceEquals(_active, this)) _active = null;
        }
    }
}
