#r "tools/bsharp/bin/Debug/net11.0/Bsharp.dll"
using System;
var solution = SolutionParser.Parse("bsharp.sln");
Console.WriteLine($"Solution: {solution.Path}");
Console.WriteLine($"Directory: {solution.Directory}");
Console.WriteLine($"Projects: {solution.Projects.Length}");
foreach (var proj in solution.Projects) {
    Console.WriteLine($"  - {proj.Name}: {proj.Path}");
    Console.WriteLine($"    Exists? {System.IO.File.Exists(proj.Path)}");
}
