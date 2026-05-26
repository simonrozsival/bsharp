// Solution file parser for bsharp.
// Parses Visual Studio .sln files to extract project information.
using System.Text.RegularExpressions;

static class SolutionParser {
    public static Solution Parse(string solutionPath) {
        if (!File.Exists(solutionPath))
            throw new FileNotFoundException($"Solution file not found: {solutionPath}");

        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var lines = File.ReadAllLines(solutionPath);
        var projects = new List<SolutionProject>();
        var configurations = new List<string>();

        // Parse projects: Project("{GUID}") = "Name", "RelativePath", "{ProjectGUID}"
        var projectRegex = new Regex(
            @"^Project\(""\{(?<TypeGuid>[^}]+)\}""\)\s*=\s*""(?<Name>[^""]+)""\s*,\s*""(?<Path>[^""]+)""\s*,\s*""\{(?<Guid>[^}]+)\}""",
            RegexOptions.Compiled
        );

        // Parse configurations: Configuration|Platform = Configuration|Platform
        var configRegex = new Regex(
            @"^\s*(?<Config>[^|]+)\|(?<Platform>[^=]+)\s*=",
            RegexOptions.Compiled
        );

        for (int i = 0; i < lines.Length; i++) {
            var line = lines[i];

            // Parse project lines
            var projectMatch = projectRegex.Match(line);
            if (projectMatch.Success) {
                var name = projectMatch.Groups["Name"].Value;
                var relativePath = projectMatch.Groups["Path"].Value;
                var guid = projectMatch.Groups["Guid"].Value;
                var typeGuid = projectMatch.Groups["TypeGuid"].Value;

                // Only include C# projects (FAE04EC0-301F-11D3-BF4B-00C04F79EFBC)
                // and SDK-style projects (9A19103F-16F7-4668-BE54-9A1E7A4F7556)
                if (relativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) {
                    // Normalize path separators for cross-platform support
                    var normalizedRelativePath = relativePath.Replace('\\', Path.DirectorySeparatorChar);
                    var fullPath = Path.GetFullPath(Path.Combine(solutionDir, normalizedRelativePath));
                    projects.Add(new SolutionProject(name, fullPath, guid));
                }
            }

            // Parse solution configurations
            if (line.Contains("GlobalSection(SolutionConfigurationPlatforms)")) {
                i++;
                while (i < lines.Length && !lines[i].Contains("EndGlobalSection")) {
                    var configMatch = configRegex.Match(lines[i]);
                    if (configMatch.Success) {
                        var config = configMatch.Groups["Config"].Value.Trim();
                        var platform = configMatch.Groups["Platform"].Value.Trim();
                        var configStr = $"{config}|{platform}";
                        if (!configurations.Contains(configStr))
                            configurations.Add(configStr);
                    }
                    i++;
                }
            }
        }

        return new Solution(
            Path.GetFullPath(solutionPath),
            solutionDir,
            projects.ToArray(),
            configurations.ToArray()
        );
    }
}

readonly record struct Solution(
    string Path,
    string Directory,
    SolutionProject[] Projects,
    string[] Configurations
);

readonly record struct SolutionProject(
    string Name,
    string Path,
    string Guid
);
