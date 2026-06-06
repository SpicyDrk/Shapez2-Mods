using System;
using Core.Localization;
using ILogger = Core.Logging.ILogger;

namespace AsteroidMaker
{
    /// <summary>
    /// PLAN-P02-001 Task 2 — the shape-code authoring surface. Opens a
    /// <c>HUDDialogSimpleInput</c> (the same dialog the vanilla Sandbox Item Producer uses
    /// for typed shape codes): the player types a code and confirms. On confirm we validate
    /// it against the CANONICAL registry (<see cref="CanonicalShapeResolver"/>); a valid
    /// code is captured into <see cref="AsteroidUiState"/> (Task 3 turns that into a
    /// placement cursor), an invalid one is rejected with an info dialog and nothing is
    /// captured — so an invalid shape can never be placed (SC-04).
    /// </summary>
    internal sealed class AsteroidAuthoringDialog
    {
        private readonly AsteroidUiState _ui;
        private readonly ILogger _logger;

        public AsteroidAuthoringDialog(AsteroidUiState ui, ILogger logger)
        {
            _ui = ui;
            _logger = logger;
        }

        /// <summary>Opens the shape-code entry dialog (the entry-selected handler).</summary>
        public void Open()
        {
            if (_ui.DialogStack == null)
            {
                _logger.Warning?.Log("[AsteroidMaker:ui] authoring dialog requested but the dialog stack isn't captured yet.");
                return;
            }

            try
            {
#pragma warning disable CS0618 // Globals.Resources — no DI seam from a mod; prefab refs are read-only.
                HUDDialogSimpleInput dialog = _ui.DialogStack.Show(Globals.Resources.UIDialogSimpleInputPrefab);
#pragma warning restore CS0618
                IText title = new RawText("Asteroid Maker");
                IText description = new RawText(
                    "Enter a shape code (e.g. CrCgCbCy:P-P-P-P-:crcgcbcy). " +
                    "Invalid codes are rejected. Colours, pins (P) and crystals (c) are supported.");
                IText buttonText = new RawText("Continue");
                IText defaultValue = new RawText(_ui.AuthoredCode ?? AsteroidUiState.DefaultShapeCode);

                dialog.Init(title, description, buttonText, defaultValue, inputCorrector: null);
                dialog.OnConfirmed.Register(OnConfirmed);
            }
            catch (Exception ex)
            {
                _logger.Error?.Log($"[AsteroidMaker:ui] failed to open authoring dialog (non-fatal): {ex}");
            }
        }

        private void OnConfirmed(string code)
        {
            try
            {
                if (CanonicalShapeResolver.TryResolve(code, out ShapeDefinition shape, out string diag))
                {
                    _ui.AuthoredCode = code.Trim();
                    _ui.AuthoredShape = shape;
                    _ui.DeleteArmed = false;    // placement + delete modes are mutually exclusive
                    _ui.PlacementArmed = true;  // hand off to the placement controller
                    _logger.Info?.Log(
                        $"[AsteroidMaker:ui] authored shape accepted: '{_ui.AuthoredCode}' ({diag}). " +
                        "Placement armed — left-click the space map to place, Esc/right-click to cancel.");
                }
                else
                {
                    _ui.AuthoredShape = null;
                    _logger.Warning?.Log($"[AsteroidMaker:ui] rejected shape code '{code}' ({diag}).");
                    ShowError(code, diag);
                }
            }
            catch (Exception ex)
            {
                _logger.Error?.Log($"[AsteroidMaker:ui] confirm handler threw (non-fatal): {ex}");
            }
        }

        private void ShowError(string code, string diag)
        {
            if (_ui.DialogStack == null) return;
            try
            {
#pragma warning disable CS0618 // Globals.Resources — no DI seam from a mod; prefab refs are read-only.
                HUDDialogSimpleInfo info = _ui.DialogStack.Show(Globals.Resources.UIDialogSimpleInfoPrefab);
#pragma warning restore CS0618
                info.Init(
                    new RawText("Invalid shape code"),
                    new RawText($"\"{code}\" isn't a valid shape code, so nothing was placed.\n\n{diag}"));
            }
            catch (Exception ex)
            {
                _logger.Error?.Log($"[AsteroidMaker:ui] failed to show invalid-code dialog (non-fatal): {ex}");
            }
        }
    }
}
