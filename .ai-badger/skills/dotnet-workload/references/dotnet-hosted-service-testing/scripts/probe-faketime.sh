#!/usr/bin/env bash
# Probe FakeTimeProvider + BackgroundService timing semantics (threading, lost advances).
# Usage: bash probe-faketime.sh
# Creates a scratch console app in a temp dir, runs it, prints thread ids + counters
# around StartAsync/Advance. Settles inline-vs-threadpool and lost-advance questions.
set -euo pipefail
DIR="$(mktemp -d)"
trap 'rm -rf "$DIR"' EXIT
cat > "$DIR/tp-probe.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.0" />
  </ItemGroup>
</Project>
EOF
cat > "$DIR/Program.cs" <<'EOF'
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Hosting;

var tp = new FakeTimeProvider(new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero));
var svc = new Loop(tp);
var cts = new CancellationTokenSource();
var run = svc.StartAsync(cts.Token);
Console.WriteLine($"after StartAsync: count={svc.Count} thread={Environment.CurrentManagedThreadId}");
tp.Advance(TimeSpan.FromMinutes(60));
Console.WriteLine($"after Advance#1: count={svc.Count} thread={Environment.CurrentManagedThreadId}");
await Task.Delay(200);
tp.Advance(TimeSpan.FromMinutes(60));
Console.WriteLine($"after Advance#2: count={svc.Count} thread={Environment.CurrentManagedThreadId}");
await Task.Delay(200);
await cts.CancelAsync();
await run;

sealed class Loop(TimeProvider time) : BackgroundService
{
    public int Count;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine($"ExecuteAsync start thread={Environment.CurrentManagedThreadId}");
        while (!stoppingToken.IsCancellationRequested)
        {
            Console.WriteLine($"about to RunOnce count={Count} thread={Environment.CurrentManagedThreadId}");
            await RunOnceAsync(stoppingToken);
            Console.WriteLine($"after RunOnce count={Count} thread={Environment.CurrentManagedThreadId}");
            await Task.Delay(TimeSpan.FromMinutes(60), time, stoppingToken);
            Console.WriteLine($"delay elapsed count={Count} thread={Environment.CurrentManagedThreadId}");
        }
    }
    private Task RunOnceAsync(CancellationToken ct) { Count++; Console.WriteLine($"RunOnce body count={Count} thread={Environment.CurrentManagedThreadId}"); return Task.CompletedTask; }
}
EOF
cd "$DIR" && dotnet run
