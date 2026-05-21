using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core.Coordinates;
using Game.Core.Map.Simulation;
using MonoMod.RuntimeDetour;
using ILogger = Core.Logging.ILogger;

namespace AnyLayerTrash
{
    /// <summary>
    /// Closes the last layer-2/3 gap by expanding <c>TrashSystem</c>'s
    /// per-instance <c>TrashInstances</c> dictionary to include all tiles of
    /// our pillar, not just its anchor (z=0).
    ///
    /// <para><b>The bug:</b> after the prediction + regular sim shims got
    /// layer-2/3 belts geometrically connected, partial behavior emerged —
    /// shapes vanished on layers 2/3 only while a layer-1 belt was also
    /// feeding. The moment the layer-1 belt was removed, all layers stopped.
    /// Hypothesis (from probe data): <c>TrashSystem.TrashInstances :
    /// Dictionary&lt;GTC, BuildingInstance&gt;</c> is keyed by the trash's
    /// anchor tile only. <c>RegisterGlobalBeltOutput(belt_pivot)</c> looks up
    /// <c>TrashInstances[target_gtc]</c> — for layer-2 belts the target is
    /// (T, z=1), not in the dict, so the per-trash belt counter
    /// (<c>BeltOutputsConnectedPerTrash</c>) never increments for those belts.
    /// Layer 1's belt is the only one that bumps the counter; remove it →
    /// counter hits zero → <c>DestroyTrashSimulation</c> tears down the sim
    /// for the whole pillar.</para>
    ///
    /// <para><b>The fix:</b> hook <c>TrashSystem.RegisterTrash</c> and after
    /// the original adds its anchor entry, reflectively add the non-anchor
    /// tiles too. Symmetric removal on <c>UnregisterTrash</c>. Belts on any
    /// layer now find the trash in the dictionary and the counter increments
    /// correctly for each.</para>
    ///
    /// <para><b>Diagnostic logging</b> stays on both the trash-register hook
    /// and the belt-register hook so we can verify in <c>Player.log</c> that
    /// (a) the expansion actually happens, (b) layer-2/3 belt registrations
    /// now fire (not just layer-1's), and (c) the counter survives layer-1
    /// removal.</para>
    ///
    /// <para>One hook applies to both TrashSystem instances (prediction-side
    /// and regular sim-side) because the methods live on the shared base
    /// class.</para>
    /// </summary>
    internal static class TrashSystemAnchorExpander
    {
        private static readonly Type? TrashSystemType =
            Type.GetType("TrashSystem, Game.Content");

        public static IReadOnlyList<Hook> Install(ILogger logger)
        {
            if (TrashSystemType == null)
            {
                logger.Warning?.Log("[AnyLayerTrash:anchor] TrashSystem type not resolvable — anchor expander not installed.");
                return Array.Empty<Hook>();
            }

            FieldInfo? trashInstancesField = TrashSystemType.GetField("TrashInstances",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (trashInstancesField == null)
            {
                logger.Warning?.Log("[AnyLayerTrash:anchor] TrashSystem.TrashInstances field not found — anchor expander not installed.");
                return Array.Empty<Hook>();
            }

            MethodInfo? registerTrash = TrashSystemType.GetMethod("RegisterTrash",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(BuildingInstance) }, null);
            MethodInfo? unregisterTrash = TrashSystemType.GetMethod("UnregisterTrash",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(BuildingInstance) }, null);
            MethodInfo? registerBelt = TrashSystemType.GetMethod("RegisterGlobalBeltOutput",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo? unregisterBelt = TrashSystemType.GetMethod("UnregisterGlobalBeltOutput",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (registerTrash == null || unregisterTrash == null || registerBelt == null || unregisterBelt == null)
            {
                logger.Warning?.Log(
                    $"[AnyLayerTrash:anchor] Could not locate all four TrashSystem methods (R={registerTrash != null}, " +
                    $"U={unregisterTrash != null}, RB={registerBelt != null}, UB={unregisterBelt != null}). " +
                    "Anchor expander not installed.");
                return Array.Empty<Hook>();
            }

            var hooks = new List<Hook>(4);

            // Hook RegisterTrash(BuildingInstance): orig first (adds anchor entry),
            // then we add extra entries for all non-anchor tiles.
            Action<Action<object, BuildingInstance>, object, BuildingInstance> registerPatch =
                (orig, self, trash) =>
                {
                    orig(self, trash);
                    ExpandTrashInstances(self, trash, trashInstancesField, logger, isRegister: true);
                };
            hooks.Add(new Hook((MethodBase)registerTrash, (Delegate)registerPatch));

            // Hook UnregisterTrash(BuildingInstance): remove extra entries first,
            // then orig (removes anchor).
            Action<Action<object, BuildingInstance>, object, BuildingInstance> unregisterPatch =
                (orig, self, trash) =>
                {
                    ExpandTrashInstances(self, trash, trashInstancesField, logger, isRegister: false);
                    orig(self, trash);
                };
            hooks.Add(new Hook((MethodBase)unregisterTrash, (Delegate)unregisterPatch));

            // Diagnostic-only hooks on the belt register/unregister to verify the fix.
            Action<Action<object, GlobalTilePivot>, object, GlobalTilePivot> registerBeltPatch =
                (orig, self, pivot) =>
                {
                    logger.Info?.Log($"[AnyLayerTrash:anchor] RegisterGlobalBeltOutput pivot={pivot} on {self.GetType().Name}");
                    orig(self, pivot);
                };
            hooks.Add(new Hook((MethodBase)registerBelt, (Delegate)registerBeltPatch));

            Action<Action<object, GlobalTilePivot>, object, GlobalTilePivot> unregisterBeltPatch =
                (orig, self, pivot) =>
                {
                    logger.Info?.Log($"[AnyLayerTrash:anchor] UnregisterGlobalBeltOutput pivot={pivot} on {self.GetType().Name}");
                    orig(self, pivot);
                };
            hooks.Add(new Hook((MethodBase)unregisterBelt, (Delegate)unregisterBeltPatch));

            logger.Info?.Log($"[AnyLayerTrash:anchor] installed {hooks.Count} TrashSystem hook(s).");
            return hooks;
        }

        private static void ExpandTrashInstances(
            object trashSystemInstance,
            BuildingInstance trash,
            FieldInfo trashInstancesField,
            ILogger logger,
            bool isRegister)
        {
            var dict = trashInstancesField.GetValue(trashSystemInstance) as Dictionary<GlobalTileCoordinate, BuildingInstance>;
            if (dict == null) return;

            IBuildingConnectorData connectorData = trash.Definition.CustomData.Get<IBuildingConnectorData>();
            TileVector[] tiles = connectorData.Tiles;
            if (tiles.Length <= 1) return;

            GlobalTileTransform transform = trash.Transform;
            GlobalTileCoordinate anchorGtc = tiles[0].ToGlobal(in transform);
            int touched = 0;
            for (int i = 1; i < tiles.Length; i++)
            {
                GlobalTileCoordinate tileGtc = tiles[i].ToGlobal(in transform);
                if (tileGtc.Equals(anchorGtc)) continue;
                if (isRegister)
                {
                    dict[tileGtc] = trash;
                    touched++;
                }
                else
                {
                    if (dict.Remove(tileGtc)) touched++;
                }
            }

            if (touched > 0)
            {
                string verb = isRegister ? "added" : "removed";
                logger.Info?.Log(
                    $"[AnyLayerTrash:anchor] {verb} {touched} non-anchor TrashInstances entries for '{trash.Definition.Id.Name}' " +
                    $"on {trashSystemInstance.GetType().Name} (anchor={anchorGtc}, tiles={tiles.Length}).");
            }
        }
    }
}
