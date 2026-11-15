namespace OTPSoftware.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? filter = args.Length > 0 ? args[0] : null;
        Console.WriteLine($"=== OTPSoftware Automated Test Runner ===");
        Console.WriteLine($"Filter: {filter ?? "(all tests)"}\n");

        Assembly testAssembly = typeof(Program).Assembly;
        var testTypes = testAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.FullName)
            .ToList();

        int passed = 0;
        int failed = 0;
        var failureDetails = new List<(string Name, Exception Ex)>();
        var sw = Stopwatch.StartNew();

        foreach (var type in testTypes)
        {
            if (filter is not null && !type.FullName!.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.GetCustomAttribute<FactAttribute>() != null)
                .ToList();

            if (methods.Count == 0) continue;

            Console.WriteLine($"[{type.Name}]");

            foreach (var method in methods)
            {
                var inlineDataAttrs = method.GetCustomAttributes<InlineDataAttribute>().ToList();

                if (inlineDataAttrs.Count > 0)
                {
                    foreach (var inlineData in inlineDataAttrs)
                    {
                        var dataRows = inlineData.GetData(method);
                        if (dataRows is null) continue;

                        foreach (var argsRow in dataRows)
                        {
                            object?[] parameters = argsRow ?? [null];
                            string paramStr = string.Join(", ", parameters.Select(p => p?.ToString() ?? "null"));
                            string testName = $"{method.Name}({paramStr})";
                            RunSingle(type, method, parameters, testName, ref passed, ref failed, failureDetails);
                        }
                    }
                }
                else
                {
                    RunSingle(type, method, null, method.Name, ref passed, ref failed, failureDetails);
                }
            }
            Console.WriteLine();
        }

        sw.Stop();
        Console.WriteLine("==================================================");
        Console.WriteLine($"TOTAL: {passed + failed} | PASSED: {passed} | FAILED: {failed} ({sw.ElapsedMilliseconds} ms)");
        Console.WriteLine("==================================================");

        if (failed > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\nFAILURES:");
            foreach (var (name, ex) in failureDetails)
            {
                Console.WriteLine($"  - {name}: {ex.GetType().Name} - {ex.Message}");
                if (ex.StackTrace is not null)
                {
                    Console.WriteLine($"    {ex.StackTrace.Split('\n').FirstOrDefault()?.Trim()}");
                }
            }
            Console.ResetColor();
        }

        return failed > 0 ? 1 : 0;
    }

    private static void RunSingle(
        Type type,
        MethodInfo method,
        object?[]? parameters,
        string testName,
        ref int passed,
        ref int failed,
        List<(string Name, Exception Ex)> failureDetails)
    {
        Console.Write($"  • {testName} ... ");
        try
        {
            object? instance = method.IsStatic ? null : Activator.CreateInstance(type);
            try
            {
                object? result = method.Invoke(instance, parameters);
                if (result is Task t)
                {
                    t.GetAwaiter().GetResult();
                }
                else if (result is ValueTask vt)
                {
                    vt.GetAwaiter().GetResult();
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("PASS");
                Console.ResetColor();
                passed++;
            }
            finally
            {
                if (instance is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("FAIL");
            Console.ResetColor();
            failed++;
            failureDetails.Add((testName, tie.InnerException));
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("FAIL");
            Console.ResetColor();
            failed++;
            failureDetails.Add((testName, ex));
        }
    }
}
