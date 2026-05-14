# XPact Engine - PCH benchmark generator. Produces N trivial .cpp files under
# Engine\Source\Programs.Targets\PCHBench that each include the heaviest of
# the C++ stdlib headers in the stub SharedPCH. Without a PCH each TU pays
# the full preprocessor cost; with a PCH each TU pays only the parse-from-PCH
# cost.

param(
    [int]$Count = 30
)
$ErrorActionPreference = "Stop"

$thisDir = Split-Path -Parent $PSCommandPath
$engineRoot = Resolve-Path (Join-Path $thisDir "..\..")
$benchDir = Join-Path $engineRoot "Source\Programs.Targets\PCHBench"

if (-not (Test-Path $benchDir)) {
    throw "PCHBench directory not found at $benchDir"
}

# Remove any previously-generated units so we always start from scratch.
Get-ChildItem -Path $benchDir -Filter "PCHBench_unit_*.cpp" -ErrorAction SilentlyContinue | Remove-Item -Force

# Each unit should be small but reference the heaviest common headers so the
# preprocessor work without PCH is non-trivial. We deliberately do NOT
# #include the stub PCH header by name: the build system /FI-forces it when
# PCH is enabled, and when PCH is disabled the TU still compiles because we
# include the same set of headers explicitly here.
$template = @'
#include <vector>
#include <string>
#include <memory>
#include <unordered_map>
#include <algorithm>
#include <iostream>
#include <sstream>
#include <functional>

namespace PCHBench {{
struct Item_{0} {{
    std::vector<std::string> labels;
    std::unordered_map<std::string, int> counts;
    int sum() const {{
        int s = 0;
        for (auto& kv : counts) {{ s += kv.second; }}
        return s;
    }}
}};

int call_{0}() {{
    Item_{0} it;
    it.labels.push_back("u{0}");
    it.counts["a"] = {0};
    return it.sum();
}}
}}
'@

for ($i = 0; $i -lt $Count; $i++) {
    $path = Join-Path $benchDir ("PCHBench_unit_{0:0000}.cpp" -f $i)
    $body = $template -f $i
    Set-Content -Path $path -Value $body -Encoding UTF8
}

Write-Host ("Generated {0} PCHBench unit files under {1}" -f $Count, $benchDir)
