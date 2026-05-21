using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Core.Simulation;
using ShapezShifter.Hijack;
using ShapezShifter.Hijack.Predictions;
using ILogger = Core.Logging.ILogger;

namespace AnyLayerTrash
{
    /// <summary>
    /// Diagnostic rewirers that dump <c>TrashSystem</c> and
    /// <c>TrashPredictionSimulationSystem</c> via reflection so we can plan
    /// a real replacement without their source. Output goes to
    /// <c>Player.log</c> with prefix <c>[AnyLayerTrash:trash-probe]</c>.
    ///
    /// <para>
    /// Both classes live in obfuscated <c>Game.Content.Trash</c> /
    /// <c>Game.Content.Trash.Prediction</c> DLLs that are not in the public
    /// decompile bundle. We need to know what we'd have to replicate before
    /// committing to a replacement: state shape, registration hooks, sim
    /// creation pattern, receiver-count source.
    /// </para>
    ///
    /// <para>
    /// Probe is harvest-only — it does not mutate the systems collection.
    /// </para>
    /// </summary>
    internal static class TrashReflectionDump
    {
        public static void DumpType(string targetName, object instance, ILogger logger)
        {
            Type t = instance.GetType();
            logger.Info?.Log($"[AnyLayerTrash:trash-probe] {targetName} TYPE: {t.FullName}, asm={t.Assembly.GetName().Name}");

            for (Type b = t.BaseType; b != null && b != typeof(object); b = b.BaseType)
            {
                logger.Info?.Log($"[AnyLayerTrash:trash-probe] {targetName}   base: {b.FullName}");
            }

            foreach (Type iface in t.GetInterfaces())
            {
                logger.Info?.Log($"[AnyLayerTrash:trash-probe] {targetName}   iface: {iface.FullName}");
            }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            for (Type walk = t; walk != null && walk != typeof(object); walk = walk.BaseType)
            {
                FieldInfo[] fields = walk.GetFields(flags);
                foreach (FieldInfo f in fields)
                {
                    string valueStr = "<reference>";
                    try
                    {
                        object v = f.GetValue(instance);
                        if (v == null)
                        {
                            valueStr = "null";
                        }
                        else if (f.FieldType.IsPrimitive || f.FieldType == typeof(string) || f.FieldType.IsEnum)
                        {
                            valueStr = v.ToString();
                        }
                        else
                        {
                            valueStr = $"<{v.GetType().Name}>";
                        }
                    }
                    catch (Exception ex)
                    {
                        valueStr = $"<read-error: {ex.GetType().Name}>";
                    }

                    logger.Info?.Log($"[AnyLayerTrash:trash-probe] {targetName}   field [{walk.Name}] {f.Name} : {Pretty(f.FieldType)} = {valueStr}");
                }

                MethodInfo[] methods = walk.GetMethods(flags);
                foreach (MethodInfo m in methods)
                {
                    if (m.IsSpecialName) continue;
                    string paramStr = string.Join(", ", m.GetParameters().Select(p => $"{Pretty(p.ParameterType)} {p.Name}"));
                    logger.Info?.Log($"[AnyLayerTrash:trash-probe] {targetName}   method [{walk.Name}] {m.Name}({paramStr}) -> {Pretty(m.ReturnType)}");
                }
            }
        }

        private static string Pretty(Type t)
        {
            if (t == null) return "<null>";
            if (!t.IsGenericType) return t.Name;
            string name = t.Name;
            int tick = name.IndexOf('`');
            if (tick > 0) name = name.Substring(0, tick);
            string args = string.Join(", ", t.GetGenericArguments().Select(Pretty));
            return $"{name}<{args}>";
        }
    }

    internal sealed class TrashSystemProbeRewirer : ISimulationSystemsRewirer
    {
        private readonly ILogger _logger;
        public TrashSystemProbeRewirer(ILogger logger) { _logger = logger; }

        public void ModifySimulationSystems(ICollection<ISimulationSystem> simulationSystems, SimulationSystemsDependencies dependencies)
        {
            _logger.Info?.Log($"[AnyLayerTrash:trash-probe] sim systems count={simulationSystems.Count}; scanning for TrashSystem.");
            foreach (ISimulationSystem sys in simulationSystems)
            {
                if (sys.GetType().Name == "TrashSystem")
                {
                    TrashReflectionDump.DumpType("TrashSystem", sys, _logger);
                    return;
                }
            }
            _logger.Warning?.Log("[AnyLayerTrash:trash-probe] TrashSystem not found in simulation systems collection.");
        }

        public bool Equals(IRewirer other) => ReferenceEquals(this, other);
    }

    internal sealed class TrashPredictionProbeRewirer : IPredictionSystemsRewirer
    {
        private readonly ILogger _logger;
        public TrashPredictionProbeRewirer(ILogger logger) { _logger = logger; }

        public void ModifyPredictionSystems(ICollection<ISimulationSystem> simulationSystems, PredictionSystemsDependencies dependencies)
        {
            _logger.Info?.Log($"[AnyLayerTrash:trash-probe] prediction systems count={simulationSystems.Count}; scanning for TrashPredictionSimulationSystem.");
            foreach (ISimulationSystem sys in simulationSystems)
            {
                if (sys.GetType().Name == "TrashPredictionSimulationSystem")
                {
                    TrashReflectionDump.DumpType("TrashPredictionSimulationSystem", sys, _logger);

                    MethodInfo create = sys.GetType().GetMethod("CreateTrashSimulation",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (create != null)
                    {
                        string paramStr = string.Join(", ", create.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                        _logger.Info?.Log($"[AnyLayerTrash:trash-probe] CreateTrashSimulation({paramStr}) -> {create.ReturnType.FullName}");

                        Type retType = create.ReturnType;
                        foreach (ConstructorInfo ctor in retType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        {
                            string ctorParams = string.Join(", ", ctor.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                            _logger.Info?.Log($"[AnyLayerTrash:trash-probe]   ret-ctor: {retType.Name}({ctorParams})");
                        }
                        foreach (PropertyInfo p in retType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        {
                            _logger.Info?.Log($"[AnyLayerTrash:trash-probe]   ret-prop: {p.Name} : {p.PropertyType.Name}");
                        }
                        foreach (FieldInfo f in retType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        {
                            _logger.Info?.Log($"[AnyLayerTrash:trash-probe]   ret-field: {f.Name} : {f.FieldType.Name}");
                        }
                    }
                    return;
                }
            }
            _logger.Warning?.Log("[AnyLayerTrash:trash-probe] TrashPredictionSimulationSystem not found in prediction systems collection.");
        }

        public bool Equals(IRewirer other) => ReferenceEquals(this, other);
    }
}
