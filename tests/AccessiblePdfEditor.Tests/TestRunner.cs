using System.Diagnostics;

namespace AccessiblePdfEditor.Tests;

// =====================================================================================
//  TestRunner.cs
//
//  A plain console test harness with no test-framework dependency.
//
//  It exists in this form because the thing being tested is an accessibility tool, and the
//  most valuable assertions are about what the program SAYS. A test that checks a heading
//  announces itself as "heading level 2, Introduction" is checking the actual product, and
//  it reads better as a sentence in a console runner than as an attribute-decorated method.
//
//  Exits non-zero on any failure, so it can gate a build.
// =====================================================================================

#region TestRunner

/// <summary>Collects and runs test cases, reporting results to the console.</summary>
public sealed class TestRunner
{
    #region State

    private readonly List<(string Group, string Name, Action Body)> _tests = [];
    private readonly List<string> _failures = [];
    private string _currentGroup = "general";

    private int _passed;
    private int _checks;

    #endregion

    #region Registration

    /// <summary>Starts a new named group. Subsequent tests are listed under it.</summary>
    public void Group(string name) => _currentGroup = name;

    /// <summary>Registers a test.</summary>
    public void Test(string name, Action body) => _tests.Add((_currentGroup, name, body));

    #endregion

    #region Assertions
    // Each reports what was expected and what happened, because a failure message that only says
    // "assertion failed" costs more time to diagnose than the test saved.

    public void IsTrue(bool condition, string what)
    {
        _checks++;
        if (!condition)
            throw new AssertionException($"{what}: expected true, was false");
    }

    public void IsFalse(bool condition, string what)
    {
        _checks++;
        if (condition)
            throw new AssertionException($"{what}: expected false, was true");
    }

    public void AreEqual<T>(T expected, T actual, string what)
    {
        _checks++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{what}: expected <{expected}>, was <{actual}>");
    }

    /// <summary>
    /// Asserts that spoken text contains a fragment, ignoring case. The workhorse of this suite:
    /// most of what matters about this program is what it says out loud.
    /// </summary>
    public void Says(string spoken, string fragment)
    {
        _checks++;
        if (!spoken.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            throw new AssertionException($"expected the announcement to mention \"{fragment}\", but it said: \"{spoken}\"");
    }

    /// <summary>Asserts that spoken text does NOT contain a fragment.</summary>
    public void DoesNotSay(string spoken, string fragment)
    {
        _checks++;
        if (spoken.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            throw new AssertionException($"expected the announcement NOT to mention \"{fragment}\", but it said: \"{spoken}\"");
    }

    public void IsNotNull(object? value, string what)
    {
        _checks++;
        if (value is null)
            throw new AssertionException($"{what}: expected a value, was null");
    }

    public void IsNull(object? value, string what)
    {
        _checks++;
        if (value is not null)
            throw new AssertionException($"{what}: expected null, was <{value}>");
    }

    #endregion

    #region Running

    /// <summary>Runs every registered test. Returns a process exit code.</summary>
    public int Run()
    {
        var stopwatch = Stopwatch.StartNew();
        string? lastGroup = null;

        foreach (var (group, name, body) in _tests)
        {
            if (group != lastGroup)
            {
                Console.WriteLine();
                Console.WriteLine($"── {group} ──");
                lastGroup = group;
            }

            try
            {
                body();
                _passed++;
                Console.WriteLine($"  PASS  {name}");
            }
            catch (AssertionException ex)
            {
                _failures.Add($"{group} / {name}: {ex.Message}");
                Console.WriteLine($"  FAIL  {name}");
                Console.WriteLine($"        {ex.Message}");
            }
            catch (Exception ex)
            {
                _failures.Add($"{group} / {name}: threw {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"  ERROR {name}");
                Console.WriteLine($"        {ex.GetType().Name}: {ex.Message}");
            }
        }

        stopwatch.Stop();

        Console.WriteLine();
        Console.WriteLine(new string('─', 60));
        Console.WriteLine($"{_passed} of {_tests.Count} tests passed, {_checks} checks, in {stopwatch.ElapsedMilliseconds} ms");

        if (_failures.Count == 0)
        {
            Console.WriteLine("All tests passed.");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine($"{_failures.Count} failure(s):");
        foreach (string failure in _failures)
            Console.WriteLine($"  • {failure}");

        return 1;
    }

    #endregion
}

#endregion

#region AssertionException

/// <summary>Thrown by a failed assertion. Carries only a message, because that is all a reader needs.</summary>
public sealed class AssertionException(string message) : Exception(message);

#endregion
