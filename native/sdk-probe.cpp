#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <cstring>

int wmain(int argc, wchar_t** argv) {
    if (argc != 3) return 10;
    auto sdk = LoadLibraryW(argv[1]); if (!sdk) return 11;
    using Initialize = bool (*)(const char*);
    using Ticket = const char* (*)();
    using Shutdown = bool (*)();
    auto init = reinterpret_cast<Initialize>(GetProcAddress(sdk, "nxapi_init"));
    auto ticket = reinterpret_cast<Ticket>(GetProcAddress(sdk, "nxapi_get_product_ticket"));
    auto alias = reinterpret_cast<Ticket>(GetProcAddress(sdk, "nxcapi_get_product_ticket"));
    auto shutdown = reinterpret_cast<Shutdown>(GetProcAddress(sdk, "nxapi_shutdown"));
    if (!init || !ticket || !alias || !shutdown) return 12;
    bool initialized = init("1050768515"); const char* first = ticket(); const char* second = alias();
    bool matches = first && second && std::strcmp(first, "KartDriftLocalOfflineIdentity00001") == 0 && std::strcmp(first, second) == 0;
    bool external = GetModuleHandleW(L"nexon_x64.dll") != nullptr;
    bool stopped = shutdown();
    FILE* report{};
    if (_wfopen_s(&report, argv[2], L"wb") != 0 || !report) return 14;
    std::fprintf(report, "{\"initialized\":%s,\"ticketMatches\":%s,\"externalRuntimeLoaded\":%s,\"shutdown\":%s}\n",
        initialized ? "true" : "false", matches ? "true" : "false", external ? "true" : "false", stopped ? "true" : "false");
    std::fclose(report);
    return initialized && matches && stopped && !external ? 0 : 13;
}
