using Game.Core.Coordinates;

namespace FourWaySplitter
{
    /// <summary>
    /// TEMPORARY scaffolding — Task 5 of PLAN-P02-002 deletes this file when
    /// it replaces the spike with real usage in building registration.
    ///
    /// Its only job is to prove, at compile time, that <see cref="TileVector"/>
    /// supports a Z axis so the hand-crafted <c>BuildingConnectorData</c> can
    /// position level-1 (upper) connectors at <c>TileVector(0, 0, 1)</c>. If
    /// this file ever fails to compile, the connector layout plan is invalid
    /// and PLAN-P02-002 must be re-planned (CONSTRAINTS §7 Stop Rule).
    /// </summary>
    internal static class ConnectorSpike
    {
        internal static readonly TileVector Level0 = new(0, 0, 0);
        internal static readonly TileVector Level1 = new(0, 0, 1);
    }
}
