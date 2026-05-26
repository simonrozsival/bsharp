// Dependency graph builder for multi-project solutions.
// Analyzes ProjectReference items to determine build order.
using System.Xml.Linq;

static class DependencyGraph {
    public static BuildGraph Build(SolutionProject[] projects) {
        var projectsByPath = projects.ToDictionary(p => p.Path, StringComparer.OrdinalIgnoreCase);
        var dependencies = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // Parse ProjectReference from each project
        foreach (var project in projects) {
            var refs = GetProjectReferences(project.Path);
            var resolvedRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var refPath in refs) {
                // Resolve relative path to absolute
                var projectDir = Path.GetDirectoryName(project.Path)!;
                var absoluteRef = Path.GetFullPath(Path.Combine(projectDir, refPath));
                
                // Only include references that are in the solution
                if (projectsByPath.ContainsKey(absoluteRef)) {
                    resolvedRefs.Add(absoluteRef);
                }
            }
            
            dependencies[project.Path] = resolvedRefs;
        }

        // Build layers (topological sort)
        var layers = ComputeLayers(projects.Select(p => p.Path).ToArray(), dependencies);
        
        return new BuildGraph(dependencies, layers);
    }

    static string[] GetProjectReferences(string projectPath) {
        if (!File.Exists(projectPath))
            return Array.Empty<string>();

        try {
            var doc = XDocument.Load(projectPath);
            return doc.Descendants()
                .Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => e.Attribute("Include")?.Value)
                .Where(v => v != null)
                .Select(v => v!.Replace('\\', Path.DirectorySeparatorChar))
                .ToArray();
        } catch {
            return Array.Empty<string>();
        }
    }

    static string[][] ComputeLayers(string[] projects, Dictionary<string, HashSet<string>> dependencies) {
        var layers = new List<List<string>>();
        var remaining = new HashSet<string>(projects, StringComparer.OrdinalIgnoreCase);
        var built = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Detect cycles
        if (HasCycle(projects, dependencies)) {
            // Fallback: single layer with all projects
            return new[] { projects };
        }

        while (remaining.Count > 0) {
            var layer = new List<string>();
            
            // Find projects with all dependencies already built
            foreach (var project in remaining) {
                var deps = dependencies[project];
                if (deps.All(d => built.Contains(d))) {
                    layer.Add(project);
                }
            }

            if (layer.Count == 0) {
                // Shouldn't happen if cycle detection works, but fail safe
                layer.AddRange(remaining);
            }

            layers.Add(layer);
            foreach (var p in layer) {
                remaining.Remove(p);
                built.Add(p);
            }
        }

        return layers.Select(l => l.ToArray()).ToArray();
    }

    static bool HasCycle(string[] projects, Dictionary<string, HashSet<string>> dependencies) {
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Visit(string project) {
            if (visited.Contains(project))
                return false;
            if (visiting.Contains(project))
                return true; // Cycle detected

            visiting.Add(project);
            foreach (var dep in dependencies[project]) {
                if (Visit(dep))
                    return true;
            }
            visiting.Remove(project);
            visited.Add(project);
            return false;
        }

        foreach (var project in projects) {
            if (Visit(project))
                return true;
        }

        return false;
    }
}

readonly record struct BuildGraph(
    Dictionary<string, HashSet<string>> Dependencies,
    string[][] Layers
) {
    public int LayerCount => Layers.Length;
    
    public string[] GetLayer(int index) => Layers[index];
    
    public IEnumerable<string> GetDependencies(string projectPath) =>
        Dependencies.TryGetValue(projectPath, out var deps) ? deps : Enumerable.Empty<string>();
}
