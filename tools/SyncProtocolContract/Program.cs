using System.Globalization;
using System.Text.Json;

namespace SyncProtocolContract;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            string command = args.ElementAtOrDefault(0) ?? "validate";
            string root = Path.GetFullPath(args.ElementAtOrDefault(1) ?? FindRepositoryRoot());
            string reportDirectory = Path.GetFullPath(args.ElementAtOrDefault(2) ?? Path.Combine(root, "artifacts", "sync-protocol-v1-validation"));
            return command switch
            {
                "generate" => Generate(root),
                "validate" => Validate(root, reportDirectory),
                _ => throw new ArgumentException("Usage: SyncProtocolContract [generate|validate] [repository-root] [report-directory]")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int Generate(string root)
    {
        FixtureGenerator.Generate(root);
        Console.WriteLine("Generated Sync Protocol v1 fixtures and manifest.");
        return 0;
    }

    private static int Validate(string root, string reportDirectory)
    {
        Directory.CreateDirectory(reportDirectory);
        ValidationReport report = FixtureRunner.Run(root);
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(reportDirectory, "report.json"), json + Environment.NewLine);
        File.WriteAllText(Path.Combine(reportDirectory, "summary.md"), report.ToMarkdown());
        Console.WriteLine($"Sync Protocol v1: {report.PassedChecks}/{report.TotalChecks} checks passed; {report.FailedChecks} failed.");
        return report.FailedChecks == 0 ? 0 : 1;
    }

    private static string FindRepositoryRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current, "sync-protocol")) || File.Exists(Path.Combine(current, "AGENTS.md"))) return current;
            current = Directory.GetParent(current)?.FullName;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
