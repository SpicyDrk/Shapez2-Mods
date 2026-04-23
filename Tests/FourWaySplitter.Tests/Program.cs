using System.Text.Json;

namespace FourWaySplitter.Tests;

/// <summary>
/// Smoke check for the FourWaySplitter mod's <c>manifest.json</c>. Mirrors
/// Tests/HelloWorld.Tests/Program.cs — same assertions, different ModName.
/// Exits 0 on all-pass, 1 otherwise.
/// </summary>
public static class Program
{
    private const string ModName = "FourWaySplitter";

    private static readonly string[] RequiredFields =
    {
        "Version",
        "Title",
        "Description",
        "Author",
        "SavedModVersionCompabilityRangeWithSelf",
        "GameVersionSupportRange",
        "AffectsSaveGames",
        "DisablesAchievements",
        "Conflicts",
        "Assemblies",
        "Dependencies",
    };

    public static int Main()
    {
        var checks = new List<Check>
        {
            new("Manifest_Exists",             Manifest_Exists),
            new("Manifest_IsValidJson",        Manifest_IsValidJson),
            new("Manifest_HasRequiredFields",  Manifest_HasRequiredFields),
            new("Manifest_DeclaresOwnDll",     Manifest_DeclaresOwnDll),
            new("Manifest_DeclaresShifterDep", Manifest_DeclaresShifterDep),
        };

        int passed = 0;
        int failed = 0;

        foreach (var check in checks)
        {
            try
            {
                check.Run();
                Console.WriteLine($"  ✓ {check.Name}");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ {check.Name}");
                Console.WriteLine($"      {ex.Message}");
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Results: {passed} passed, {failed} failed ({checks.Count} total)");
        return failed == 0 ? 0 : 1;
    }

    // ---- Individual checks ----------------------------------------------

    private static void Manifest_Exists()
    {
        string path = LocateManifest();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Expected manifest at: {path}");
        }
    }

    private static void Manifest_IsValidJson()
    {
        using JsonDocument doc = LoadManifest();
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Manifest root must be an object, got {doc.RootElement.ValueKind}");
        }
    }

    private static void Manifest_HasRequiredFields()
    {
        using JsonDocument doc = LoadManifest();
        var missing = RequiredFields
            .Where(f => !doc.RootElement.TryGetProperty(f, out _))
            .ToList();
        if (missing.Count > 0)
        {
            throw new InvalidDataException(
                $"Manifest missing required field(s): {string.Join(", ", missing)}");
        }
    }

    private static void Manifest_DeclaresOwnDll()
    {
        using JsonDocument doc = LoadManifest();
        JsonElement assemblies = doc.RootElement.GetProperty("Assemblies");
        if (assemblies.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Manifest.Assemblies must be an array.");
        }
        bool found = assemblies.EnumerateArray()
            .Any(a => a.GetString() == $"{ModName}.dll");
        if (!found)
        {
            throw new InvalidDataException(
                $"Manifest.Assemblies should include '{ModName}.dll'.");
        }
    }

    private static void Manifest_DeclaresShifterDep()
    {
        using JsonDocument doc = LoadManifest();
        JsonElement deps = doc.RootElement.GetProperty("Dependencies");
        if (deps.ValueKind != JsonValueKind.Array || deps.GetArrayLength() == 0)
        {
            throw new InvalidDataException(
                "Manifest.Dependencies must be a non-empty array.");
        }
        bool hasShifter = deps.EnumerateArray().Any(d =>
            d.TryGetProperty("ModTitle", out JsonElement title) &&
            title.GetString() is { } s &&
            s.Contains("Shifter", StringComparison.OrdinalIgnoreCase));
        if (!hasShifter)
        {
            throw new InvalidDataException(
                "Manifest.Dependencies should declare ShapezShifter.");
        }
    }

    // ---- Helpers --------------------------------------------------------

    /// <summary>
    /// Walk up from the executable's directory until we find the directory
    /// containing <c>Shapez2Mods.sln</c> (the repo root), then compute
    /// <c>Mods/{ModName}/manifest.json</c>.
    /// </summary>
    private static string LocateManifest()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Shapez2Mods.sln")))
        {
            dir = dir.Parent;
        }
        if (dir == null)
        {
            throw new DirectoryNotFoundException(
                "Could not locate repo root (Shapez2Mods.sln not found walking up from " +
                AppContext.BaseDirectory + ").");
        }
        return Path.Combine(dir.FullName, "Mods", ModName, "manifest.json");
    }

    private static JsonDocument LoadManifest()
    {
        return JsonDocument.Parse(File.ReadAllText(LocateManifest()));
    }

    private sealed record Check(string Name, Action Run);
}
