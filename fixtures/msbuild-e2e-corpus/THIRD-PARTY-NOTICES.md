# dotnet/msbuild E2E corpus

This directory vendors a curated subset of test assets from the `dotnet/msbuild`
repository for bsharp head-to-head validation.

- Upstream repository: <https://github.com/dotnet/msbuild>
- Upstream commit: `911bea0b57d3613eb9c29f49ff9858d03884c397`
- Upstream source path: `src/MSBuild.EndToEnd.Tests/TestAssets`
- License: MIT; see `LICENSE.dotnet-msbuild`.

The test runner copies these assets to temporary directories and applies the
normalization steps recorded in `corpus.json` before running builds.
