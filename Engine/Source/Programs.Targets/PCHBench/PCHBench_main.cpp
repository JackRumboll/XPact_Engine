// PCHBench - main entry point. Restricted to the stub PCH header set so both
// PCH-on and PCH-off builds compile this exact source set.
#include <vector>
#include <string>
#include <memory>
#include <cstdint>
#include <cstring>

namespace PCHBench {
int run_main() {
    std::vector<std::string> v{ "PCH", "Bench" };
    std::size_t total = 0;
    for (auto& s : v) { total += s.size(); }
    return static_cast<int>(total);
}
}

int main() {
    return PCHBench::run_main();
}
