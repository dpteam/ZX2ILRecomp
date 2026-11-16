using System.Diagnostics;

namespace ZX2ILRecomp.Tests;

/// <summary>
/// Minimal assertion harness.
///
/// A test framework would be a third-party package, and the whole point of this
/// project is that it has none. What a framework actually buys here is a
/// non-zero exit code and a readable failure — both of which fit in one file.
/// </summary>
public static class TestHarness
{
    private sealed record TestCase(string Name, Action Body);

    private static readonly List<TestCase> Cases = new();

    public static void Test(string name, Action body)
    {
        Cases.Add(new TestCase(name, body));
    }

    public static int RunAll(string suiteName)
    {
        Console.WriteLine();
        Console.WriteLine(suiteName);
        Console.WriteLine(new string('=', suiteName.Length));
        Console.WriteLine();

        int passed = 0;
        int failed = 0;
        Stopwatch clock = Stopwatch.StartNew();

        foreach (TestCase test in Cases)
        {
            try
            {
                test.Body();
                passed++;
                Console.WriteLine("  PASS  " + test.Name);
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine("  FAIL  " + test.Name);
                Console.WriteLine("        " + ex.GetType().Name + ": " + ex.Message);

                if (ex.StackTrace is not null)
                {
                    foreach (string line in ex.StackTrace.Split('\n').Take(4))
                    {
                        Console.WriteLine("        " + line.Trim());
                    }
                }
            }
        }

        clock.Stop();

        Console.WriteLine();
        Console.WriteLine(string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "{0} passed, {1} failed, {2} total in {3} ms",
            passed,
            failed,
            Cases.Count,
            clock.ElapsedMilliseconds));

        return failed == 0 ? 0 : 1;
    }

    public static void True(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException("expected true: " + what);
        }
    }

    public static void False(bool condition, string what)
    {
        if (condition)
        {
            throw new InvalidOperationException("expected false: " + what);
        }
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                what + ": expected <" + expected + ">, got <" + actual + ">");
        }
    }

    public static void NotNull(object? value, string what)
    {
        if (value is null)
        {
            throw new InvalidOperationException(what + ": expected non-null");
        }
    }

    public static void Contains(string haystack, string needle, string what)
    {
        if (!haystack.Contains(needle, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(what + ": expected to find \"" + needle + "\"");
        }
    }

    public static void DoesNotContain(string haystack, string needle, string what)
    {
        if (haystack.Contains(needle, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(what + ": did not expect \"" + needle + "\"");
        }
    }

    /// <summary>Creates a throwaway directory that is deleted on dispose.</summary>
    public static TempDirectory Temp()
    {
        return new TempDirectory();
    }
}

/// <summary>A temp directory that cleans itself up.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "zx2il-tests-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Leftovers in temp are harmless.
        }
    }
}
