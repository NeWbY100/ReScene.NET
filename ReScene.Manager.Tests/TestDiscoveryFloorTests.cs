using System.Reflection;

namespace ReScene.Manager.Tests;

/// <summary>
/// A floor under how many tests this assembly exposes, so a run that discovers fewer than it should
/// fails instead of reporting a smaller green number.
/// <para>
/// COUNTING RULE, stated because a count without one cannot be reproduced: this counts METHODS
/// carrying a test attribute — <c>Fact</c>, <c>Theory</c>, <c>AvaloniaFact</c> or
/// <c>AvaloniaTheory</c>, matched by attribute type NAME so framework-derived attributes are
/// included — across every non-abstract class in the assembly. A theory counts ONCE regardless of
/// how many data rows it expands to, so this number is deliberately smaller than the run's reported
/// total and moves only when someone adds or removes a test method.
/// </para>
/// <para>
/// WHAT THIS DOES NOT CATCH, and this matters because of how it came to be written. It does NOT
/// guard the incident that prompted it. When a scratch output path outside the repository broke six
/// source-reading censuses, all <b>523</b> tests were still discovered and the six FAILED, loudly,
/// with <c>DirectoryNotFoundException</c> — measured, <c>Failed: 6, Passed: 517, Skipped: 0,
/// Total: 523</c>. The discovered count was unchanged, so this floor would have passed. It guards a
/// different and real failure mode: test methods that stop being discovered at all — a file dropped
/// from compilation, a stray <c>--filter</c> left in a CI invocation, an attribute renamed by a
/// framework upgrade. Those produce a smaller GREEN run, and a smaller green number is the one
/// nobody questions.
/// </para>
/// <para>
/// One case deliberately NOT claimed, because checking it turned up the answer: a test class made
/// non-public does not need this guard. xUnit's own analyzer makes it a BUILD ERROR (xUnit1000,
/// "Test classes must be public"), so it can never reach a run. Break-verified instead against a
/// case that can happen — removing one test file from compilation drops the count to 499 and fails
/// here by name.
/// </para>
/// </summary>
public class TestDiscoveryFloorTests
{
    /// <summary>
    /// The number of test METHODS this assembly is known to expose, per the counting rule above.
    /// Raise it deliberately when tests are added; a drop is a defect until proven otherwise.
    /// </summary>
    private const int DiscoveryFloor = 501;

    private static readonly string[] TestAttributeNames =
        ["FactAttribute", "TheoryAttribute", "AvaloniaFactAttribute", "AvaloniaTheoryAttribute"];

    [Fact]
    public void ThisAssembly_ExposesAtLeastItsKnownTestCount()
    {
        int methods = CountTestMethods(typeof(TestDiscoveryFloorTests).Assembly);

        Assert.True(methods >= DiscoveryFloor,
            $"only {methods} test methods were found in {typeof(TestDiscoveryFloorTests).Assembly.GetName().Name}, " +
            $"below the known floor of {DiscoveryFloor}. Tests have stopped being discovered — check that no " +
            "class was made non-public or abstract, no file left the compilation, and no filter is narrowing " +
            $"the run. If tests were deliberately removed, lower {nameof(DiscoveryFloor)} in the same commit.");
    }

    // PUBLIC and non-abstract, which is xUnit's own discovery rule. Counting every type
    // Assembly.GetTypes() returns would include internal classes that xUnit never runs — the floor
    // would then stay put while a class quietly stopped being discovered, which is the exact failure
    // this guard claims to catch. Proven by sabotage: making one test class internal drops the count.
    internal static int CountTestMethods(Assembly assembly) =>
        assembly.GetTypes()
            .Where(t => (t.IsPublic || t.IsNestedPublic) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Count(m => m.GetCustomAttributes(inherit: true)
                .Any(a => TestAttributeNames.Contains(a.GetType().Name, StringComparer.Ordinal)));
}
