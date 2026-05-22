#!/usr/bin/env bash
set -euo pipefail

output_dir="${1:-artifacts/fixtures/console-10k}"
count="${2:-10000}"

if ! [[ "$count" =~ ^[0-9]+$ ]] || [[ "$count" -lt 1 ]]; then
  echo "count must be a positive integer" >&2
  exit 2
fi

rm -rf "$output_dir"
mkdir -p "$output_dir/Chain"

cat > "$output_dir/console-10k.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
    <RootNamespace>Console10k</RootNamespace>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
EOF

cat > "$output_dir/Program.cs" <<'EOF'
Console.WriteLine(Console10k.C00000.Get());
EOF

last=$((count - 1))
for ((i = 0; i < count; i++)); do
  n="$(printf '%05d' "$i")"
  path="$output_dir/Chain/C$n.cs"
  if [[ "$i" -eq "$last" ]]; then
    cat > "$path" <<EOF
namespace Console10k;

public static class C$n
{
    public static int Get() => 1;
}
EOF
  else
    next="$(printf '%05d' "$((i + 1))")"
    cat > "$path" <<EOF
namespace Console10k;

public static class C$n
{
    public static int Get() => C$next.Get() + 1;
}
EOF
  fi
done

echo "Generated $((count + 1)) C# files in $output_dir"
