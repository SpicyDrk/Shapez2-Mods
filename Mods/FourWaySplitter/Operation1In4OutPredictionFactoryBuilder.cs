namespace FourWaySplitter
{
    /// <summary>
    /// STUB — intentional placeholder for a future 1-in-4-out prediction.
    ///
    /// The game's prediction framework caps at
    /// <c>Processing1In2OutPredictionSimulation</c> — there is no
    /// <c>Processing1In4OutPredictionSimulation</c>. Implementing a proper
    /// 1-in-4-out prediction requires a custom <c>IItemPredictionSimulation</c>
    /// type built from the primitives, which is out of scope for PLAN-P02-002.
    ///
    /// TODO(v2): Implement a dedicated prediction simulation so predictive
    /// routing (belt preview / build-mode pathing) sees the 4 outputs. Until
    /// then, the splitter works at simulation-time only; prediction-time the
    /// game sees no outputs (acceptable risk for v1 — player can still place
    /// and wire the building, just no predictive belt pathing through it).
    ///
    /// This file is kept empty (no interface implementation) on purpose.
    /// PLAN-P02-002 Task 5 decides whether
    /// <c>AtomicBuildings.Extend().WithSimulation(...)</c> requires a
    /// prediction factory builder at all — if it does, that's a Task 5 STOP,
    /// not a Task 3 problem.
    /// </summary>
    internal static class Operation1In4OutPredictionFactoryBuilder
    {
    }
}

