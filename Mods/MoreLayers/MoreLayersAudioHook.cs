using System;
using System.Reflection;
using MonoMod.RuntimeDetour;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace MoreLayers
{
    /// <summary>
    /// Fixes the actual remaining per-frame <c>IndexOutOfRangeException</c>
    /// traced via UAT-P02 Plan-007 diagnostic logging.
    ///
    /// <para>
    /// Decompiled <c>MapSoundManager.UpdateClusterEntities</c> calls
    /// <c>Settings.ScoreByHeight[buildingTile.BuildingLayer()]</c> per visible
    /// building per frame (decompiled <c>MapSoundManager.cs:394</c>). The
    /// backing <c>MapSoundSettings.ScoreByHeight</c> field is hardcoded:
    /// <code>public float[] ScoreByHeight = new float[3] { 0.8f, 0.9f, 1f };</code>
    /// Once the player places a building on layer ≥ 3 (now reachable post-cap-raise),
    /// the array access throws IOORE every frame the building is visible.
    /// The exception cascades up through <c>GameAudioManager.Update</c> →
    /// <c>GameSessionOrchestrator.Tick</c> (the same Tick that drives
    /// rendering); a per-frame thrown exception in the Tick chain disrupts
    /// downstream rendering and audio updates.
    /// </para>
    ///
    /// <para>
    /// Fix: postfix-hook <c>MapSoundManager</c>'s constructor. After the orig
    /// runs, replace <c>self.Settings.ScoreByHeight</c> with a size-7 array,
    /// padding indices 3..6 with the last existing value (1f for vanilla).
    /// Idempotent — runs once per <c>MapSoundManager</c> instance, but the
    /// Settings reference is typically a shared ScriptableObject so the
    /// mutation persists across the session.
    /// </para>
    /// </summary>
    internal sealed class MoreLayersAudioHook : IDisposable
    {
        private const int TargetSize = 7; // matches MoreLayersScenarioRewirer.TargetCap (6) + 1

        private readonly Hook _hook;

        public MoreLayersAudioHook(ILogger logger)
        {
            ConstructorInfo ctor = typeof(MapSoundManager)
                .GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: new[]
                    {
                        typeof(IMapModel),
                        typeof(Viewport),
                        typeof(AudioListener),
                        typeof(BuildingSoundManager),
                        typeof(ISoundPlayer),
                        typeof(ISimulationSpeedReader),
                        typeof(MapSoundSettings),
                        typeof(ILogger),
                    },
                    modifiers: null)
                ?? throw new InvalidOperationException(
                    "MoreLayers: failed to find MapSoundManager 8-arg ctor.");

            CtorDelegate detour = CtorPostfix;
            _hook = new Hook(ctor, detour);

            logger.Info?.Log("MoreLayers: audio hook installed on MapSoundManager ctor (extends MapSoundSettings.ScoreByHeight 3 → 7).");
        }

        public void Dispose()
        {
            _hook.Dispose();
        }

        private delegate void CtorDelegate(
            Action<MapSoundManager, IMapModel, Viewport, AudioListener, BuildingSoundManager, ISoundPlayer, ISimulationSpeedReader, MapSoundSettings, ILogger> orig,
            MapSoundManager self,
            IMapModel map,
            Viewport viewport,
            AudioListener audioListener,
            BuildingSoundManager soundManager,
            ISoundPlayer soundPlayer,
            ISimulationSpeedReader simulationSpeed,
            MapSoundSettings settings,
            ILogger logger);

        private static void CtorPostfix(
            Action<MapSoundManager, IMapModel, Viewport, AudioListener, BuildingSoundManager, ISoundPlayer, ISimulationSpeedReader, MapSoundSettings, ILogger> orig,
            MapSoundManager self,
            IMapModel map,
            Viewport viewport,
            AudioListener audioListener,
            BuildingSoundManager soundManager,
            ISoundPlayer soundPlayer,
            ISimulationSpeedReader simulationSpeed,
            MapSoundSettings settings,
            ILogger logger)
        {
            orig(self, map, viewport, audioListener, soundManager, soundPlayer, simulationSpeed, settings, logger);

            // settings is the same MapSoundSettings instance the orig stored on self.Settings.
            // ScoreByHeight is a public mutable float[]; direct assignment works.
            if (settings == null) return;
            float[]? scores = settings.ScoreByHeight;
            if (scores == null || scores.Length == 0 || scores.Length >= TargetSize) return;

            float[] extended = new float[TargetSize];
            int copy = scores.Length;
            for (int i = 0; i < copy; i++) extended[i] = scores[i];
            float fb = scores[copy - 1];
            for (int i = copy; i < TargetSize; i++) extended[i] = fb;

            settings.ScoreByHeight = extended;

            logger?.Info?.Log($"MoreLayers: extended MapSoundSettings.ScoreByHeight from {copy} to {TargetSize} (fallback={fb}).");
        }
    }
}
