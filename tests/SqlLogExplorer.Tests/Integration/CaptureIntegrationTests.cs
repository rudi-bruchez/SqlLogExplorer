using System.Diagnostics;
using System.Text.Json;

namespace SqlLogExplorer.Tests.Integration;

public class CaptureIntegrationTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tests", "fixtures", "capture.sh"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Racine du dépôt introuvable.");
    }

    [IntegrationFact]
    public void Capture_2022_ProducesANonEmptyLogForEveryScenario()
    {
        var root = RepoRoot();
        var output = Path.Combine(Path.GetTempPath(), "sle-capture-" + Guid.NewGuid().ToString("N"));
        try
        {
            var psi = new ProcessStartInfo("bash") { RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = root };
            psi.ArgumentList.Add(Path.Combine(root, "tests", "fixtures", "capture.sh"));
            psi.ArgumentList.Add("2022");
            psi.Environment["SLE_FIXTURE_OUT"] = output;
            using var process = Process.Start(psi)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEndAsync();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, await_(stderr) + await_(stdout));

            var scenarios = Directory.GetFiles(Path.Combine(root, "tests", "fixtures", "scenarios"), "*.sql")
                .Select(f => Path.GetFileNameWithoutExtension(f)).ToList();
            Assert.NotEmpty(scenarios);
            foreach (var scenario in scenarios)
            {
                var file = Path.Combine(output, "2022", scenario + ".json");
                Assert.True(File.Exists(file), $"Fixture manquante : {scenario}");
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                Assert.True(doc.RootElement.GetProperty("Log").GetArrayLength() > 0, $"Journal vide : {scenario}");
            }
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, true);
        }

        static string await_(Task<string> t) => t.GetAwaiter().GetResult();
    }
}
