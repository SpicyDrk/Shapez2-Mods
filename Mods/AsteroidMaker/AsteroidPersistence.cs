using System;
using System.Collections.Generic;
using Core.Factory;
using Game.Core.Coordinates;
using ShapezShifter.Flow;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace AsteroidMaker
{
    /// <summary>PLAN-P03-001 Task 1 — JSON-serializable save blob: the placed custom asteroids.</summary>
    public sealed class AsteroidSaveData
    {
        public List<PlacedAsteroidRecord> Asteroids { get; set; } = new List<PlacedAsteroidRecord>();
    }

    /// <summary>One placed asteroid: origin chunk + authored code + the exact footprint offsets.</summary>
    public sealed class PlacedAsteroidRecord
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Z { get; set; }
        public string Code { get; set; } = "";
        public List<TileOffset> Tiles { get; set; } = new List<TileOffset>();
    }

    /// <summary>A footprint tile, relative to the record's origin (z is always the origin layer).</summary>
    public sealed class TileOffset
    {
        public int X { get; set; }
        public int Y { get; set; }
    }

    internal sealed class AsteroidSaveDataFactory : IFactory<AsteroidSaveData>
    {
        public AsteroidSaveData Produce() => new AsteroidSaveData();
    }

    /// <summary>
    /// PLAN-P03-001 Task 1 — persistence for placed custom asteroids (SC-06).
    ///
    /// <para>Custom asteroids placed in open space live in non-island super-chunks, which the
    /// vanilla serializer does NOT write (<c>SuperChunkSerializer.Serialize</c> only persists
    /// chunks where <c>ContainsIslands</c>), so they'd vanish on reload. We own their persistence
    /// via Shifter's <see cref="ModSaveDataRewirer{T}"/> — a per-save JSON blob. The registry
    /// (<see cref="AsteroidSaveData"/>) is the source of truth: a record is appended on each
    /// placement, removed on delete/undo, and re-injected into the live resource map on load.</para>
    ///
    /// <para><b>Re-inject timing.</b> The load (<c>AfterSaveDataDeserialized</c>), the
    /// <c>ResourcesMap</c> capture, and the game's own <c>GameResourcesMap.Deserialize</c> fire in
    /// an order we can't guarantee — and a too-early injection would be wiped when Deserialize
    /// clears the chunk dictionary. So after a load we re-ensure every record for a short settle
    /// window (driven by <see cref="AsteroidPersistenceTick"/>): each tick, any record whose
    /// origin doesn't currently resolve to a source is (re)added (idempotent), and the window ends
    /// once all records have been stably present for a few frames.</para>
    /// </summary>
    internal sealed class AsteroidPersistence : IDisposable
    {
        private const int SettleFramesAfterLoad = 300; // ~5s ceiling — covers load-order races
        private const int StableFramesToStop = 30;     // stop ~0.5s after all records are present

        private readonly AsteroidUiState _ui;
        private readonly ILogger _logger;

        private bool _dataLoaded;
        private int _settleFramesLeft;
        private int _stableFrames;

        public ModSaveDataRewirer<AsteroidSaveData> SaveRewirer { get; }
        public AsteroidPersistenceTick SettleTick { get; }

        public AsteroidPersistence(AsteroidUiState ui, ILogger logger)
        {
            _ui = ui;
            _logger = logger;
            SaveRewirer = new ModSaveDataRewirer<AsteroidSaveData>(
                "asteroid-maker", new AsteroidSaveDataFactory(), logger);
            SaveRewirer.AfterSaveDataDeserialized.Register(OnAfterLoad);
            SettleTick = new AsteroidPersistenceTick(this);
        }

        private AsteroidSaveData Data => SaveRewirer.Data;

        /// <summary>
        /// Append a record for a freshly placed asteroid (called by the placement controller).
        /// Returns the created record so the caller can push it onto the undo stack.
        /// </summary>
        public PlacedAsteroidRecord RecordPlacement(GlobalChunkCoordinate origin, IReadOnlyList<ChunkVector> offsets, string code)
        {
            var rec = new PlacedAsteroidRecord { X = origin.x, Y = origin.y, Z = origin.z, Code = code ?? "" };
            foreach (ChunkVector o in offsets) rec.Tiles.Add(new TileOffset { X = o.x, Y = o.y });
            Data.Asteroids.Add(rec);
            _logger.Info?.Log(
                $"[AsteroidMaker:save] recorded '{code}' at ({origin.x},{origin.y},{origin.z}) " +
                $"({rec.Tiles.Count} tiles); total tracked={Data.Asteroids.Count}.");
            return rec;
        }

        /// <summary>
        /// Remove the registry record whose footprint covers <paramref name="tile"/> (used by
        /// delete/undo). Returns the removed record, or null if none matched.
        /// </summary>
        public PlacedAsteroidRecord? RemoveRecordCovering(GlobalChunkCoordinate tile)
        {
            List<PlacedAsteroidRecord> list = Data.Asteroids;
            for (int i = 0; i < list.Count; i++)
            {
                PlacedAsteroidRecord rec = list[i];
                if (rec.Z != tile.z) continue;
                foreach (TileOffset t in rec.Tiles)
                {
                    if (rec.X + t.X == tile.x && rec.Y + t.Y == tile.y)
                    {
                        list.RemoveAt(i);
                        return rec;
                    }
                }
            }
            return null;
        }

        /// <summary>Re-append a previously removed record (used by undo of a delete / redo of a place).</summary>
        public void ReAddRecord(PlacedAsteroidRecord rec)
        {
            if (rec != null) Data.Asteroids.Add(rec);
        }

        /// <summary>
        /// Remove a specific record instance from the registry (used by undo of a place / redo of a
        /// delete, where we hold the exact record and want no footprint-matching ambiguity). Returns
        /// true if it was present.
        /// </summary>
        public bool RemoveRecordExact(PlacedAsteroidRecord rec) => rec != null && Data.Asteroids.Remove(rec);

        /// <summary>Called by the capture rewirer once the live ResourcesMap is available.</summary>
        public void OnResourcesMapReady()
        {
            if (_dataLoaded) TryReinject();
        }

        private void OnAfterLoad(AsteroidSaveData data)
        {
            _dataLoaded = true;
            _settleFramesLeft = SettleFramesAfterLoad;
            _stableFrames = 0;
            int count = data?.Asteroids?.Count ?? 0;
            _logger.Info?.Log(
                $"[AsteroidMaker:save] loaded {count} tracked asteroid(s); re-injecting over the settle window.");
            TryReinject();
        }

        /// <summary>Driven by <see cref="AsteroidPersistenceTick"/> — re-ensure records during the settle window.</summary>
        internal void TickSettle()
        {
            if (_settleFramesLeft <= 0) return;
            _settleFramesLeft--;
            TryReinject();
        }

        private void TryReinject()
        {
            if (!_dataLoaded) return;
            if (_ui.ResourcesMap is not GameResourcesMap grm) return;

            AsteroidSaveData data = Data;
            if (data?.Asteroids == null || data.Asteroids.Count == 0)
            {
                _settleFramesLeft = 0;
                return;
            }

            int injected = 0, present = 0, failed = 0;
            foreach (PlacedAsteroidRecord rec in data.Asteroids)
            {
                var origin = new GlobalChunkCoordinate(rec.X, rec.Y, (short)rec.Z);
                MapSuperChunk chunk = grm.GetOrCreateSuperChunkAt_SC(origin.To_SC());

                if (chunk.GetResourceSource(origin) != null) { present++; continue; }

                if (!CanonicalShapeResolver.TryResolve(rec.Code, out ShapeDefinition shape, out _))
                {
                    failed++;
                    continue;
                }

                var offsets = new List<ChunkVector>(rec.Tiles.Count);
                foreach (TileOffset t in rec.Tiles) offsets.Add(new ChunkVector(t.X, t.Y, 0));
                if (offsets.Count == 0) offsets.Add(new ChunkVector(0, 0, 0));

                if (AsteroidPlacer.TryAddSource(grm, shape, origin, offsets, _logger, out _, out _))
                    injected++;
                else
                    failed++;
            }

            if (injected > 0 || failed > 0)
            {
                _logger.Info?.Log(
                    $"[AsteroidMaker:save] re-inject pass: {injected} added, {present} present, {failed} failed " +
                    $"(of {data.Asteroids.Count}).");
            }

            // Stop the settle window once everything has been present for a few stable frames.
            if (present == data.Asteroids.Count && failed == 0)
            {
                if (++_stableFrames >= StableFramesToStop) _settleFramesLeft = 0;
            }
            else
            {
                _stableFrames = 0;
            }
        }

        public void Dispose() => SaveRewirer.Dispose();
    }

    /// <summary>Drives <see cref="AsteroidPersistence.TickSettle"/> during the post-load settle window.</summary>
    internal sealed class AsteroidPersistenceTick : ITickRewirer
    {
        private readonly AsteroidPersistence _persistence;

        public AsteroidPersistenceTick(AsteroidPersistence persistence)
        {
            _persistence = persistence;
        }

        public void Tick(float deltaTime) => _persistence.TickSettle();

        public bool Equals(IRewirer other) => ReferenceEquals(this, other);
    }
}
