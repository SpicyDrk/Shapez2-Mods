using System;
using System.Reflection;
using Game.Core.Rendering.MeshGeneration;
using MonoMod.RuntimeDetour;
using ILogger = Core.Logging.ILogger;

namespace MoreLayers
{
    /// <summary>
    /// Fixes the static-mesh-disappearance bug surfaced by UAT-P02 Test 5.
    ///
    /// <para>
    /// Real root cause (PLAN-P02-002 incorrectly targeted bounds): per-frame
    /// <c>IndexOutOfRangeException</c> at <c>StaticBuildingMeshBuilder.BuildBaseMesh</c>:
    /// <code>
    /// MainMeshPerLayer[tile_G.BuildingLayer()]
    /// </code>
    /// The <c>MainMeshPerLayer</c> array is allocated in
    /// <c>BuildingDrawDataFactory.FromMeta</c> with a hardcoded size of 3
    /// (`int num = 3;` at decompiled line 28). When the player places a building
    /// on layer ≥ 3, the lookup IOORE-throws inside <c>BuildBaseMesh</c>; the
    /// exception propagates up through <c>GenerateStaticContentsMesh</c> →
    /// <c>DrawStaticContentsMesh</c> → <c>Draw</c>, aborting the chunk's
    /// combined-mesh generation. With no cached combined mesh, the entire
    /// chunk's static content fails to draw — every static mesh on the platform
    /// vanishes per frame.
    /// </para>
    ///
    /// <para>
    /// Fix: postfix-hook <c>BuildingDrawDataFactory.FromMeta</c>. After the
    /// original returns, replace <c>BuildingDrawData.MainMeshPerLayer</c> via
    /// reflection with an extended size-7 array, filling indices 3..6 by
    /// duplicating the highest existing layer's mesh (<c>originalArray[2]</c>).
    /// </para>
    ///
    /// <para>
    /// Why duplicate-last-mesh rather than calling <c>BuildingMeshGenerator.ComputeMainMesh</c>:
    /// for <c>IndividualMainMeshPerLayer = true</c> definitions (belts and similar
    /// per-layer-stand-height buildings), <c>ComputeMainMesh</c> returns
    /// <c>LODEmptyMesh</c> when <c>layer >= MainMeshPerLayerLOD.Count</c> (= 3 vanilla).
    /// PLAN-P02-003 used that helper and produced invisible meshes for layer-4+
    /// belts. Duplicating <c>originalArray[2]</c> works for both conventions:
    /// shared-mesh buildings already have the single shared mesh at every index
    /// 0..2 (so the duplicate is the same mesh — no behavior change); per-layer
    /// buildings get the layer-3 mesh as a visual approximation for layers 4-6
    /// (visible, possibly slightly imperfect on per-layer details — that polish
    /// is a P03 concern).
    /// </para>
    ///
    /// <para>
    /// Single call site for <c>FromMeta</c> is <c>BuildingDefinitionFactory.cs:57</c>,
    /// invoked per-building-definition during scenario load — after our mod has
    /// loaded, so our hook is in place when buildings are processed.
    /// </para>
    /// </summary>
    internal sealed class MoreLayersDrawDataHook : IDisposable
    {
        private const int TargetSize = 7; // matches MoreLayersScenarioRewirer.TargetCap (6) + 1f for layer-0 indexing

        private static readonly FieldInfo MainMeshPerLayerField =
            typeof(BuildingDrawData).GetField(
                "<MainMeshPerLayer>k__BackingField",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "MoreLayers: failed to reflect BuildingDrawData.<MainMeshPerLayer>k__BackingField. " +
                "Game version may have changed the auto-property compilation.");

        private readonly Hook _hook;

        public MoreLayersDrawDataHook(ILogger logger)
        {
            // BuildingDrawDataFactory.FromMeta is a public static method:
            //   public static BuildingDrawData FromMeta(VisualThemeBaseResources, IMetaBuildingDefinition,
            //                                          IBuildingConnectorData, IMeshCache, bool)
            MethodInfo originalMethod = typeof(BuildingDrawDataFactory)
                .GetMethod(
                    nameof(BuildingDrawDataFactory.FromMeta),
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: new[]
                    {
                        typeof(VisualThemeBaseResources),
                        typeof(IMetaBuildingDefinition),
                        typeof(IBuildingConnectorData),
                        typeof(IMeshCache),
                        typeof(bool),
                    },
                    modifiers: null)
                ?? throw new InvalidOperationException(
                    "MoreLayers: failed to find BuildingDrawDataFactory.FromMeta with expected signature.");

            FromMetaDelegate detour = FromMetaPostfix;
            _hook = new Hook(originalMethod, detour);

            logger.Info?.Log("MoreLayers: draw-data hook installed on BuildingDrawDataFactory.FromMeta (extends MainMeshPerLayer 3 → 7 per-building).");
        }

        public void Dispose()
        {
            _hook.Dispose();
        }

        // For static methods, the orig delegate has no `self` param.
        private delegate BuildingDrawData FromMetaDelegate(
            Func<VisualThemeBaseResources, IMetaBuildingDefinition, IBuildingConnectorData, IMeshCache, bool, BuildingDrawData> orig,
            VisualThemeBaseResources resources,
            IMetaBuildingDefinition definition,
            IBuildingConnectorData connectorData,
            IMeshCache meshCache,
            bool mirrored);

        private static BuildingDrawData FromMetaPostfix(
            Func<VisualThemeBaseResources, IMetaBuildingDefinition, IBuildingConnectorData, IMeshCache, bool, BuildingDrawData> orig,
            VisualThemeBaseResources resources,
            IMetaBuildingDefinition definition,
            IBuildingConnectorData connectorData,
            IMeshCache meshCache,
            bool mirrored)
        {
            // Side-effect once-per-process: extend the shared VisualThemeBaseResources
            // BeltCap*/PipeStands*/WireCap* size-3 arrays before the orig call so the
            // factory's downstream consumers (StaticBuildingMeshBuilder.BuildEndCaps,
            // BuildingMeshGenerator.*PreviewMesh) see the extended arrays from frame 1.
            // Idempotent: reflective scan early-exits on arrays already at size 7.
            ExtendThemeResourceArrays(resources);

            BuildingDrawData result = orig(resources, definition, connectorData, meshCache, mirrored);

            ILODMesh[] originalArray = result.MainMeshPerLayer;
            if (originalArray == null || originalArray.Length >= TargetSize)
            {
                // Defensive: null array (shouldn't happen) or already-extended (re-entry).
                return result;
            }

            ILODMesh[] extended = new ILODMesh[TargetSize];
            int copyCount = originalArray.Length;
            for (int i = 0; i < copyCount; i++)
            {
                extended[i] = originalArray[i];
            }
            // Fill new slots by duplicating the highest existing layer's mesh.
            // For shared-mesh buildings the duplicate IS the shared mesh (no change).
            // For IndividualMainMeshPerLayer=true buildings (belts), this yields the
            // layer-3 mesh as a visual approximation for layers 4-6 — visible, slight
            // per-layer-detail imperfection is acceptable for v1 (P03 polish).
            ILODMesh fallback = originalArray[copyCount - 1];
            for (int i = copyCount; i < TargetSize; i++)
            {
                extended[i] = fallback;
            }

            MainMeshPerLayerField.SetValue(result, extended);
            return result;
        }

        /// <summary>
        /// Extend every <c>public LODMeshAsset[]</c> field on
        /// <see cref="VisualThemeBaseResources"/> from size 3 to size 7 by duplicating
        /// index 2 into indices 3..6. Catches all the BeltCap*/PipeStands*/WireCap*
        /// arrays (annotated <c>[RequiredListLength(3)]</c> in the decompiled source)
        /// without naming them individually — robust to game updates that add new
        /// same-shape size-3 arrays. Idempotent: arrays already at size ≥ 7 are skipped.
        /// </summary>
        private static void ExtendThemeResourceArrays(VisualThemeBaseResources resources)
        {
            if (resources == null) return;

            FieldInfo[] fields = typeof(VisualThemeBaseResources).GetFields(
                BindingFlags.Public | BindingFlags.Instance);
            foreach (FieldInfo field in fields)
            {
                if (field.FieldType != typeof(LODMeshAsset[])) continue;
                if (!(field.GetValue(resources) is LODMeshAsset[] arr)) continue;
                if (arr.Length >= TargetSize) continue;
                if (arr.Length == 0) continue; // can't pad an empty array

                LODMeshAsset[] extended = new LODMeshAsset[TargetSize];
                int copy = arr.Length;
                for (int i = 0; i < copy; i++) extended[i] = arr[i];
                LODMeshAsset fb = arr[copy - 1];
                for (int i = copy; i < TargetSize; i++) extended[i] = fb;
                field.SetValue(resources, extended);
            }
        }
    }
}
