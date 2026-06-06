using System;
using System.Reflection;
using MonoMod.RuntimeDetour;
using ILogger = Core.Logging.ILogger;

namespace CustomAsteroids
{
    /// <summary>
    /// PLAN-P04-001 (UAT fix) — locks the space-map mouse-drag pan while a custom-asteroid
    /// placement is armed, so the player can drag a box to size the asteroid without the camera
    /// panning out from under the cursor.
    ///
    /// <para><b>Why.</b> Vanilla platform placement owns the left-drag: its placement tracker
    /// consumes the <c>camera.mouse-drag-modifier</c> keybinding each frame, so
    /// <c>CameraController.Update_MouseMovement</c> sees it already consumed and skips the pan. Our
    /// custom <see cref="CustomAsteroidPlacementInitiator"/> ends immediately (it just fires a
    /// callback), so the engine never runs a tracker for us and the drag pans the camera —
    /// making a reliable box-select impossible. Rather than reproduce the input-context plumbing,
    /// we prefix-hook the single mouse-drag-pan method and no-op it while
    /// <see cref="CustomAsteroidUiState.PlacementArmed"/> is set. Keyboard / edge-scroll / zoom
    /// panning (separate methods) are untouched, matching how vanilla placement leaves those live.</para>
    /// </summary>
    internal sealed class CustomAsteroidCameraLock : IDisposable
    {
        private readonly Hook _hook;
        private readonly CustomAsteroidUiState _ui;
        private readonly ILogger _logger;

        public CustomAsteroidCameraLock(CustomAsteroidUiState ui, ILogger logger)
        {
            _ui = ui;
            _logger = logger;

            MethodInfo method = typeof(CameraController).GetMethod(
                "Update_MouseMovement",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                ?? throw new InvalidOperationException(
                    "CustomAsteroids: failed to find CameraController.Update_MouseMovement(InputDownstreamContext).");

            PanDelegate detour = PanPrefix;
            _hook = new Hook(method, detour);

            logger.Info?.Log("[CustomAsteroids:place] camera-pan lock installed (mouse-drag pan suppressed while placement is armed).");
        }

        public void Dispose() => _hook.Dispose();

        private delegate void PanDelegate(
            Action<CameraController, InputDownstreamContext> orig,
            CameraController self,
            InputDownstreamContext context);

        private void PanPrefix(
            Action<CameraController, InputDownstreamContext> orig,
            CameraController self,
            InputDownstreamContext context)
        {
            // While a placement is armed, the left-drag belongs to our box-select — suppress the
            // mouse-drag camera pan (other pan paths stay live). Otherwise behave exactly as vanilla.
            if (_ui.PlacementArmed) return;
            orig(self, context);
        }
    }
}
