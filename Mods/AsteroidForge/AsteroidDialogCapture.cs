using System;
using System.Reflection;
using MonoMod.RuntimeDetour;
using Unity.Core.Prefabs;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace AsteroidForge
{
    /// <summary>
    /// PLAN-P02-001 Task 2 — captures the HUD's <c>IHUDDialogStack</c> so the mod can open
    /// dialogs. An <c>IMod</c> only receives an <c>ILogger</c> and has no DI access to
    /// HUD-side services, and there's no global accessor — so we postfix-hook
    /// <c>HUDDialogStack</c>'s constructor and stash the instance (mirrors the
    /// MoreLayers/AnyLayerTrash ctor-hook pattern).
    /// </summary>
    internal sealed class AsteroidDialogCapture : IDisposable
    {
        private readonly Hook _hook;
        private readonly AsteroidUiState _ui;
        private readonly ILogger _logger;

        public AsteroidDialogCapture(AsteroidUiState ui, ILogger logger)
        {
            _ui = ui;
            _logger = logger;

            ConstructorInfo ctor = typeof(HUDDialogStack).GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(Transform), typeof(IPrefabInstanceProvider), typeof(ILogger) },
                modifiers: null)
                ?? throw new InvalidOperationException(
                    "AsteroidForge: failed to find HUDDialogStack(Transform, IPrefabInstanceProvider, ILogger) ctor.");

            CtorDelegate detour = CtorPostfix;
            _hook = new Hook(ctor, detour);

            logger.Info?.Log("[AsteroidForge:ui] dialog-stack capture hook installed on HUDDialogStack ctor.");
        }

        public void Dispose()
        {
            _hook.Dispose();
        }

        private delegate void CtorDelegate(
            Action<HUDDialogStack, Transform, IPrefabInstanceProvider, ILogger> orig,
            HUDDialogStack self,
            Transform parent,
            IPrefabInstanceProvider provider,
            ILogger logger);

        private void CtorPostfix(
            Action<HUDDialogStack, Transform, IPrefabInstanceProvider, ILogger> orig,
            HUDDialogStack self,
            Transform parent,
            IPrefabInstanceProvider provider,
            ILogger logger)
        {
            orig(self, parent, provider, logger);

            _ui.DialogStack = self;
            _logger.Info?.Log("[AsteroidForge:ui] captured IHUDDialogStack (authoring dialog is now available).");
        }
    }
}
