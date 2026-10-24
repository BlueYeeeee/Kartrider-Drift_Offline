#define KD_NATIVE_TEST
#include "compat.cpp"
#include <cstdio>
#include <fstream>
#include <thread>

extern "C" {
    void *testPolyTarget{}, *testPresetTarget{}, *testNoticeTarget{};
    void CallPolyFixture(void*); void PolyFixtureAfter();
    bool CallPresetFixture(void*); void PresetFixtureAfter();
    void CallNoticeFixture(void*, void*, void*, void*); void NoticeFixtureAfter();
}
namespace {
volatile LONG checks{};
volatile LONG allocated{}, released{}, ctorCalls{};
NativeStatus counters{};
void Check(bool value, const char* label) { if (!value) { std::fprintf(stderr, "FAIL: %s\n", label); std::exit(1); } InterlockedIncrement(&checks); }
void* Allocate(size_t count) { auto p = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, count); Check(p != nullptr, "fixture allocation"); InterlockedIncrement(&allocated); return p; }
void TestFree(void* value) { if (value) { HeapFree(GetProcessHeap(), 0, value); InterlockedIncrement(&released); } }
void* TestCopy(FArray* out, Range* range) {
    out->count = out->capacity = static_cast<int>(range->end - range->begin);
    out->data = static_cast<FString*>(Allocate(sizeof(FString) * out->count));
    for (int i = 0; i < out->count; i++) {
        auto& target = out->data[i]; auto& source = range->begin[i];
        target.count = target.capacity = source.count; target.data = static_cast<wchar_t*>(Allocate(source.count * sizeof(wchar_t)));
        std::memcpy(target.data, source.data, source.count * sizeof(wchar_t));
    }
    return out;
}
void Seed(void* self) {
    *reinterpret_cast<FArray*>(static_cast<unsigned char*>(self) + 0x638) = PrepareArray({L"ko-kr", L"zh-hant"});
    *reinterpret_cast<FArray*>(static_cast<unsigned char*>(self) + 0x648) = PrepareArray({L"한국어", L"繁體中文"});
}
void Cleanup(void* self) {
    FreeArray(*reinterpret_cast<FArray*>(static_cast<unsigned char*>(self) + 0x638));
    FreeArray(*reinterpret_cast<FArray*>(static_cast<unsigned char*>(self) + 0x648));
}
__declspec(noinline) std::uint64_t FixtureCtor(void* self, std::uint64_t a, double b, std::uint64_t c, std::uint64_t d, double e, std::uint64_t f, std::uint64_t h) {
    if (a != 0x1122334455667788ULL || b != 1.25 || c != 0xaabbccddeeff0011ULL || d != 17 || e != 2.5 || f != 29 || h != 37) return 0;
    Seed(self); InterlockedIncrement(&ctorCalls); return 0xfedcba9876543210ULL;
}
__declspec(noinline) double FixtureLanguage(void*, double a, double b, double c, double d, double e) { return a + 2 * b + 3 * c + 4 * d + 5 * e; }
__declspec(noinline) void FixturePoly(void* self) { volatile auto value = *static_cast<unsigned char*>(self); (void)value; }
__declspec(noinline) bool FixturePreset(void* self) { volatile auto value = *static_cast<unsigned char*>(self); return value != 0; }
volatile LONG notices[256]{};
__declspec(noinline) void FixtureNotice(void*, void*, void*, void* type) { InterlockedIncrement(&notices[*static_cast<unsigned char*>(type)]); }
json ReadJson(const wchar_t* path) { std::ifstream file(path); return json::parse(file); }
}

int wmain(int argc, wchar_t** argv) {
    Check(argc == 2, "captured vehicle-switch fixture required"); status = &counters;
    Check(MH_Initialize() == MH_OK, "MinHook initializes");
    copyArray = TestCopy; engineFree = TestFree;
    Check(CreateHook(reinterpret_cast<void*>(FixtureCtor), reinterpret_cast<void*>(CtorEnter), &originalCtor), "constructor hook creation");
    Check(CreateHook(reinterpret_cast<void*>(FixtureLanguage), reinterpret_cast<void*>(LanguageEnter), &originalLanguage), "language hook creation");
    Check(CreateHook(reinterpret_cast<void*>(FixturePoly), reinterpret_cast<void*>(PolyglotEnter), &originalPolyglot), "polyglot hook creation");
    Check(CreateHook(reinterpret_cast<void*>(FixturePreset), reinterpret_cast<void*>(PresetEnter), &originalPreset), "preset hook creation");
    Check(CreateHook(reinterpret_cast<void*>(FixtureNotice), reinterpret_cast<void*>(NoticeEnter), &originalNotice), "notice hook creation");
    Check(MH_EnableHook(MH_ALL_HOOKS) == MH_OK, "test hooks enabled");
    alignas(16) unsigned char object[0x658]{};
    using Constructor = decltype(&FixtureCtor);
    volatile Constructor invokeCtor = FixtureCtor;
    Check(invokeCtor(object, 0x1122334455667788ULL, 1.25, 0xaabbccddeeff0011ULL, 17, 2.5, 29, 37) == 0xfedcba9876543210ULL,
        "constructor GP, XMM, stack arguments and full RAX return preserved");
    Check(ArrayEquals(*reinterpret_cast<FArray*>(object + 0x638), {L"ko-kr", L"zh-hant", L"en-US", L"zh-Hans"}), "language codes expanded in native allocator memory");
    Check(ArrayEquals(*reinterpret_cast<FArray*>(object + 0x648), {L"한국어", L"繁體中文", L"English", L"简体中文"}), "parallel language labels expanded");
    int expanded = counters.expandedLanguages;
    using Language = decltype(&FixtureLanguage); volatile Language invokeLanguage = FixtureLanguage;
    Check(invokeLanguage(object, 1.25, 2.5, 3.75, 4.25, 5.5) == 62.0, "entry hook preserves XMM arguments and floating return");
    Check(counters.expandedLanguages == expanded && depth == 0, "language extension idempotent and TLS frame balanced");
    Cleanup(object);

    auto captured = ReadJson(argv[1]); json request, response;
    for (auto& value : captured) if (value.value("reqId", 0) == 477) {
        if (value.value("_type", "") == "PqItemPresetOnUpdate") request = value;
        if (value.value("_type", "") == "PrItemPresetOnUpdate") response = value;
    }
    Check(!request.is_null() && !response.is_null(), "original captured failure loaded");
    Check(VehicleDifference(request, response, "Kart:401010041"), "captured vehicle-parts restoration accepted");
    Check(!VehicleDifference(request, response, "Kart:401010017"), "same-car edit keeps native validation");
    Check(!VehicleDifference(request, response, ""), "unknown previous vehicle keeps native validation");
    for (int n = 0; n < 5; n++) {
        auto changed = response;
        if (n == 0) changed["result"]["code"] = 10001;
        if (n == 1) changed["reqId"] = 478;
        if (n == 2) changed["itemPreset"]["itemMap"]["Kart"] = "Kart:401010041";
        if (n == 3) changed["itemPreset"]["itemMap"]["Costume"] = "Costume:unowned";
        if (n == 4) changed["itemPreset"]["itemMap"]["UnknownSlot"] = "unexpected";
        Check(!VehicleDifference(request, changed, "Kart:401010041"), "unrelated restoration rejected");
    }
    auto packet = [&](const json& value, bool incoming) { auto text = value.dump(); ObservePacket(reinterpret_cast<const unsigned char*>(text.data()), static_cast<ULONG>(text.size()), incoming); };
    packet({{"_type", "PrLogin"}, {"itemPresetOn", {{"itemMap", {{"Kart", "Kart:401010041"}}}}}}, true);
    packet(request, false); packet(response, true);
    testPresetTarget = reinterpret_cast<void*>(FixturePreset);
    game = reinterpret_cast<unsigned char*>(PresetFixtureAfter) - 0x1fd4a79;
    unsigned char falseValue = 0, trueValue = 1;
    using Preset = decltype(&FixturePreset); volatile Preset invokePreset = FixturePreset;
    Check(!invokePreset(&falseValue), "other preset caller unchanged");
    Check(CallPresetFixture(&falseValue), "only the verified caller consumes a bounded restoration");
    Check(!CallPresetFixture(&falseValue), "restoration consumed once");
    packet({{"_type", "PrLogin"}, {"itemPresetOn", {{"itemMap", {{"Kart", "Kart:401010041"}}}}}}, true);
    packet(request, false); packet(response, true); expires = GetTickCount64() - 1;
    Check(!CallPresetFixture(&falseValue), "expired restoration rejected");
    Check(CallPresetFixture(&trueValue), "native success remains success");

    testPolyTarget = reinterpret_cast<void*>(FixturePoly);
    game = reinterpret_cast<unsigned char*>(PolyFixtureAfter) - 0x1b959b6;
    alignas(16) unsigned char poly[0xa0]{}; *reinterpret_cast<const wchar_t**>(poly + 0x18) = L"String";
    CallPolyFixture(poly); Check(poly[0x98] == 1, "verified Polyglot caller preserves localized resource fallback");
    poly[0x98] = 0; using Poly = decltype(&FixturePoly); volatile Poly invokePoly = FixturePoly; invokePoly(poly);
    Check(poly[0x98] == 0, "other Polyglot caller unchanged");
    *reinterpret_cast<const wchar_t**>(poly + 0x18) = L"Unrelated"; CallPolyFixture(poly);
    Check(poly[0x98] == 0, "other namespace unchanged");
    testNoticeTarget = reinterpret_cast<void*>(FixtureNotice);
    game = reinterpret_cast<unsigned char*>(NoticeFixtureAfter) - 0x1bc9bcf;
    for (unsigned i = 0; i < 256; i++) { auto type = static_cast<unsigned char>(i); CallNoticeFixture(nullptr, nullptr, nullptr, &type); }
    Check(notices[119] == 0 && counters.blockedNotices == 1, "timer playtime notice alone filtered");
    for (unsigned i = 0; i < 256; i++) if (i != 119) Check(notices[i] == 1, "other notice types forwarded");
    unsigned char type = 119; using NoticeFn = decltype(&FixtureNotice); volatile NoticeFn invokeNotice = FixtureNotice;
    invokeNotice(nullptr, nullptr, nullptr, &type); Check(notices[119] == 1, "other caller's playtime notice forwarded");

    auto memory = VirtualAlloc(nullptr, 4096, MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE);
    std::memset(memory, 0xcc, 32);
    Check(!DirectReplace(memory, reinterpret_cast<void*>(ProductTicket), "40574883ec20488bf94885c9750833c0"), "version mismatch refuses to patch");
    Check(static_cast<unsigned char*>(memory)[0] == 0xcc, "mismatched bytes remain intact"); VirtualFree(memory, 0, MEM_RELEASE);
    std::vector<std::thread> threads;
    for (int i = 0; i < 4; i++) threads.emplace_back([&] {
        alignas(16) unsigned char self[0x658]{};
        auto result = invokeCtor(self, 0x1122334455667788ULL, 1.25, 0xaabbccddeeff0011ULL, 17, 2.5, 29, 37);
        if (result != 0xfedcba9876543210ULL || depth != 0) std::abort(); Cleanup(self);
    });
    for (auto& thread : threads) thread.join();
    Check(ctorCalls == 5, "TLS return frames isolated across four concurrent threads");
    Check(allocated == released, "all test allocator-owned language memory freed exactly once");
    Check(counters.error == 0, "no native compatibility error");
    MH_Uninitialize();
    std::printf("{\"passed\":true,\"checks\":%d,\"gameClientStarted\":false,\"sdkTestSeparate\":true}\n", checks);
    return 0;
}
