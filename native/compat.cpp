#include "compat.h"
#include "vendor/minhook/include/MinHook.h"
#include "vendor/nlohmann/json.hpp"
#include <bcrypt.h>
#include <intrin.h>
#include <string>
#include <vector>
#include <cstring>

using json = nlohmann::json;
extern "C" {
    void *originalCtor{}, *originalLanguage{}, *originalPolyglot{}, *originalPreset{}, *originalNotice{};
}
namespace {
NativeStatus* status{};
unsigned char* game{};
volatile PVOID sdkModule{}, analyticsModule{}, cryptoModule{};
volatile LONG sdkEpoch{}, analyticsEpoch{}, doneSdk{}, doneAnalytics{};
void* notificationCookie{};
using Notify = LONG (NTAPI*)(ULONG, void (CALLBACK*)(ULONG, const void*, void*), void*, void**);
struct UString { USHORT length, capacity; wchar_t* data; };
struct DllInfo { ULONG flags; const UString* full; const UString* name; void* base; ULONG size; };
struct FString { wchar_t* data; int count, capacity; };
struct FArray { FString* data; int count, capacity; };
struct Range { FString* begin; FString* end; };
using CopyArray = void* (*)(FArray*, Range*);
using Free = void (*)(void*);
CopyArray copyArray{};
Free engineFree{};
SRWLOCK presetLock = SRWLOCK_INIT;
std::string currentKart;
json pendingRequest;
std::int64_t pendingId{};
bool hasPending{}, armed{};
ULONGLONG expires{};
struct Frame { unsigned kind; void* self; void* caller; bool restore; };
__declspec(thread) Frame frames[32];
__declspec(thread) unsigned depth;
__declspec(thread) bool inCallback;
constexpr char identity[] = "KartDriftLocalOfflineIdentity00001";

void Fail(LONG code) { if (status) InterlockedCompareExchange(&status->error, code, 0); }
bool Match(const void* address, const void* bytes, size_t size) {
    __try { return address && std::memcmp(address, bytes, size) == 0; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
bool HexMatch(const void* address, const char* hex) {
    unsigned char bytes[32]{}; size_t size = std::strlen(hex) / 2;
    if (size > sizeof bytes) return false;
    auto digit = [](char c) { return c <= '9' ? c - '0' : c - 'a' + 10; };
    for (size_t i = 0; i < size; i++) bytes[i] = static_cast<unsigned char>((digit(hex[i * 2]) << 4) | digit(hex[i * 2 + 1]));
    return Match(address, bytes, size);
}
// The loader callback only publishes module pointers. Export parsing and code
// updates happen on our worker, without loader APIs while the callback waits.
void* Export(void* module, const char* name) {
    auto base = static_cast<unsigned char*>(module);
    auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    auto nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    auto directory = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT];
    auto exports = reinterpret_cast<IMAGE_EXPORT_DIRECTORY*>(base + directory.VirtualAddress);
    auto names = reinterpret_cast<DWORD*>(base + exports->AddressOfNames);
    auto ordinals = reinterpret_cast<WORD*>(base + exports->AddressOfNameOrdinals);
    auto functions = reinterpret_cast<DWORD*>(base + exports->AddressOfFunctions);
    for (DWORD i = 0; i < exports->NumberOfNames; i++)
        if (std::strcmp(reinterpret_cast<char*>(base + names[i]), name) == 0) {
            DWORD rva = functions[ordinals[i]];
            return rva >= directory.VirtualAddress && rva < directory.VirtualAddress + directory.Size ? nullptr : base + rva;
        }
    return nullptr;
}
bool DirectReplace(void* target, void* replacement, const char* prefix) {
    if (!HexMatch(target, prefix)) return false;
    unsigned char jump[14]{0xff, 0x25, 0, 0, 0, 0};
    std::memcpy(jump + 6, &replacement, sizeof replacement);
    DWORD old{}; if (!VirtualProtect(target, sizeof jump, PAGE_EXECUTE_READWRITE, &old)) return false;
    std::memcpy(target, jump, sizeof jump);
    DWORD ignored{}; bool restored = VirtualProtect(target, sizeof jump, old, &ignored) != FALSE;
    return FlushInstructionCache(GetCurrentProcess(), target, sizeof jump) && restored;
}
bool InitializeSdk(void*) { InterlockedIncrement(&status->sdkInitializations); return true; }
const char* ProductTicket() { InterlockedIncrement(&status->tickets); return identity; }
bool DisableAnalytics(void*, void*, void*) { return false; }
bool InstallSdk(void* module) {
    void* init = Export(module, "nxapi_init");
    void* ticket = Export(module, "nxapi_get_product_ticket");
    void* alias = Export(module, "nxcapi_get_product_ticket");
    if (!HexMatch(init, "40574883ec20488bf94885c9750833c0") ||
        !HexMatch(ticket, "4883ec28488b0d6d1303004885c97507") ||
        !HexMatch(alias, "4883ec28488b0dcd1203004885c97504")) return false;
    return DirectReplace(init, reinterpret_cast<void*>(InitializeSdk), "40574883ec20488bf94885c9750833c0") &&
        DirectReplace(ticket, reinterpret_cast<void*>(ProductTicket), "4883ec28488b0d6d1303004885c97507") &&
        DirectReplace(alias, reinterpret_cast<void*>(ProductTicket), "4883ec28488b0dcd1203004885c97504");
}
bool NameIs(const UString* value, const wchar_t* expected) {
    if (!value) return false;
    size_t size = wcslen(expected);
    if (value->length != size * sizeof(wchar_t)) return false;
    for (size_t i = 0; i < size; i++) {
        wchar_t c = value->data[i]; if (c >= L'A' && c <= L'Z') c += L'a' - L'A';
        if (c != expected[i]) return false;
    }
    return true;
}
void CALLBACK ModuleChanged(ULONG reason, const void* data, void*) {
    const auto* info = static_cast<const DllInfo*>(data);
    if (NameIs(info->name, L"nexon_api_x64.dll")) {
        if (reason == 1) {
            InterlockedExchangePointer(&sdkModule, info->base);
            LONG epoch = InterlockedIncrement(&sdkEpoch);
            // SDK adaptation must complete before the loading thread calls it.
            // No OS/loader calls here. The worker never takes the loader lock.
            while (doneSdk != epoch && status->error == 0) YieldProcessor();
        } else if (reason == 2) InterlockedExchangePointer(&sdkModule, nullptr);
    } else if (NameIs(info->name, L"nexonanalytics64.dll")) {
        if (reason == 1) {
            InterlockedExchangePointer(&analyticsModule, info->base);
            LONG epoch = InterlockedIncrement(&analyticsEpoch);
            while (doneAnalytics != epoch && status->error == 0) YieldProcessor();
        } else if (reason == 2) InterlockedExchangePointer(&analyticsModule, nullptr);
    } else if (NameIs(info->name, L"bcrypt.dll")) {
        if (reason == 1) InterlockedExchangePointer(&cryptoModule, info->base);
        else if (reason == 2) InterlockedExchangePointer(&cryptoModule, nullptr);
    }
}

std::wstring ReadString(const FString& value) {
    if (value.count < 1 || value.count > 80 || !value.data || value.capacity < value.count) throw std::runtime_error("language string");
    return std::wstring(value.data, value.count - 1);
}
bool ArrayEquals(const FArray& value, const std::vector<std::wstring>& expected) {
    if (value.count != static_cast<int>(expected.size()) || !value.data || value.capacity < value.count) return false;
    for (int i = 0; i < value.count; i++) if (ReadString(value.data[i]) != expected[i]) return false;
    return true;
}
FArray PrepareArray(const std::vector<std::wstring>& values) {
    std::vector<FString> items;
    for (auto& value : values) items.push_back({const_cast<wchar_t*>(value.c_str()), static_cast<int>(value.size() + 1), static_cast<int>(value.size() + 1)});
    Range range{items.data(), items.data() + items.size()}; FArray result{};
    copyArray(&result, &range); return result;
}
void FreeArray(FArray& array) {
    for (int i = 0; i < array.count; i++) engineFree(array.data[i].data);
    engineFree(array.data); array = {};
}
void ExtendLanguages(void* settings) {
    auto codes = reinterpret_cast<FArray*>(static_cast<unsigned char*>(settings) + 0x638);
    auto names = reinterpret_cast<FArray*>(static_cast<unsigned char*>(settings) + 0x648);
    const std::vector<std::wstring> allCodes{L"ko-kr", L"zh-hant", L"en-US", L"zh-Hans"};
    const std::vector<std::wstring> allNames{L"한국어", L"繁體中文", L"English", L"简体中文"};
    if (ArrayEquals(*codes, allCodes) && ArrayEquals(*names, allNames)) return;
    if (!ArrayEquals(*codes, {L"ko-kr", L"zh-hant"}) || !ArrayEquals(*names, {L"한국어", L"繁體中文"})) { Fail(210); return; }
    auto newCodes = PrepareArray(allCodes), newNames = PrepareArray(allNames);
    FArray oldCodes = *codes, oldNames = *names;
    *codes = newCodes; *names = newNames;
    FreeArray(oldCodes); FreeArray(oldNames);
    InterlockedIncrement(&status->expandedLanguages);
}
bool VehicleDifference(const json& request, const json& response, const std::string& previous) {
    if (request.value("_type", "") != "PqItemPresetOnUpdate" || response.value("_type", "") != "PrItemPresetOnUpdate" ||
        request.at("reqId") != response.at("reqId") || response.at("result").value("code", -1) != 0 || previous.empty()) return false;
    const auto& before = request.at("itemPreset").at("itemMap"); const auto& after = response.at("itemPreset").at("itemMap");
    if (!before.contains("Kart") || before.at("Kart") == previous || !after.contains("Kart") || after.at("Kart") != before.at("Kart")) return false;
    const char* allowed[]{"PartBack", "PartFront", "PartSide", "PartHandle", "PartPlate", "PartWheel", "KartAura", "KartBooster", "SkidMark"};
    bool changed = false;
    auto compare = [&](const json& left, const json& right) {
        for (auto it = left.begin(); it != left.end(); ++it) {
            if (right.contains(it.key()) && it.value() == right.at(it.key())) continue;
            changed = true; bool accepted = false;
            for (auto name : allowed) if (it.key() == name) accepted = true;
            if (!accepted) return false;
        }
        return true;
    };
    return compare(before, after) && compare(after, before) && changed;
}
void ObservePacket(const unsigned char* data, ULONG size, bool response) {
    if (!data || size < 2 || size > 4 * 1024 * 1024 || data[0] != '{') return;
    // A native frame may include a trailing request marker; match the old parser.
    while (size && data[size - 1] != '}') size--;
    auto value = json::parse(data, data + size, nullptr, false);
    if (value.is_discarded() || !value.is_object()) return;
    const auto type = value.value("_type", "");
    if (!response && type != "PqItemPresetOnUpdate") return;
    if (response && type != "PrLogin" && type != "PrItemPresetOnUpdate") return;
    AcquireSRWLockExclusive(&presetLock);
    try {
        if (!response) { pendingRequest = value; pendingId = value.at("reqId").get<std::int64_t>(); hasPending = true; armed = false; }
        else if (type == "PrLogin") {
            currentKart = value.at("itemPresetOn").at("itemMap").value("Kart", "");
            InterlockedIncrement(&status->logins);
        } else {
            armed = hasPending && value.at("reqId").get<std::int64_t>() == pendingId && VehicleDifference(pendingRequest, value, currentKart);
            hasPending = false; pendingRequest = nullptr; expires = GetTickCount64() + 2000;
            if (value.at("result").value("code", -1) == 0 && value.contains("itemPreset"))
                currentKart = value.at("itemPreset").at("itemMap").value("Kart", currentKart);
        }
    } catch (...) { armed = hasPending = false; pendingRequest = nullptr; }
    ReleaseSRWLockExclusive(&presetLock);
}
using CryptFn = NTSTATUS (WINAPI*)(BCRYPT_KEY_HANDLE, PUCHAR, ULONG, VOID*, PUCHAR, ULONG, PUCHAR, ULONG, ULONG*, ULONG);
CryptFn originalEncrypt{}, originalDecrypt{};
NTSTATUS WINAPI Encrypt(BCRYPT_KEY_HANDLE key, PUCHAR input, ULONG size, VOID* padding, PUCHAR iv, ULONG ivSize, PUCHAR output, ULONG outputSize, ULONG* result, ULONG flags) {
    try { ObservePacket(input, size, false); } catch (...) { Fail(220); }
    return originalEncrypt(key, input, size, padding, iv, ivSize, output, outputSize, result, flags);
}
NTSTATUS WINAPI Decrypt(BCRYPT_KEY_HANDLE key, PUCHAR input, ULONG size, VOID* padding, PUCHAR iv, ULONG ivSize, PUCHAR output, ULONG outputSize, ULONG* result, ULONG flags) {
    NTSTATUS code = originalDecrypt(key, input, size, padding, iv, ivSize, output, outputSize, result, flags);
    if (code == 0 && output && result) try { ObservePacket(output, *result, true); } catch (...) { Fail(221); }
    return code;
}
bool CreateHook(void* target, void* detour, void** original) { return MH_CreateHook(target, detour, original) == MH_OK; }
bool InstallCrypto(void* module) {
    return CreateHook(Export(module, "BCryptEncrypt"), reinterpret_cast<void*>(Encrypt), reinterpret_cast<void**>(&originalEncrypt)) &&
        CreateHook(Export(module, "BCryptDecrypt"), reinterpret_cast<void*>(Decrypt), reinterpret_cast<void**>(&originalDecrypt)) && MH_EnableHook(MH_ALL_HOOKS) == MH_OK;
}
bool GameCodeReady() {
    return HexMatch(game + 0x1a8c8e0, "48895c24084889742410574883ec308b") &&
        HexMatch(game + 0x1461cb0, "48896c242057415441564883ec20") &&
        HexMatch(game + 0x1a71050, "48896c242057415441564883ec20") &&
        HexMatch(game + 0x24289e0, "48895c241855565741564157488dac24") &&
        HexMatch(game + 0x25e8f70, "48895c24084889742410574883ec20") &&
        HexMatch(game + 0x25f32ca, "4180be98000000000f85") &&
        HexMatch(game + 0x25ca440, "4885c9742e534883ec20488bd9488b0d");
}
bool InstallGameHooks() {
    copyArray = reinterpret_cast<CopyArray>(game + 0x1461cb0); engineFree = reinterpret_cast<Free>(game + 0x25ca440);
    return CreateHook(game + 0x24289e0, reinterpret_cast<void*>(CtorEnter), &originalCtor) &&
        CreateHook(game + 0x1a71050, reinterpret_cast<void*>(LanguageEnter), &originalLanguage) &&
        CreateHook(game + 0x25e8f70, reinterpret_cast<void*>(PolyglotEnter), &originalPolyglot) && MH_EnableHook(MH_ALL_HOOKS) == MH_OK;
}
bool InstallPreset() {
    return CreateHook(game + 0x1e27e40, reinterpret_cast<void*>(PresetEnter), &originalPreset) && MH_EnableHook(MH_ALL_HOOKS) == MH_OK;
}
bool InstallNotice() {
    return CreateHook(game + 0x1bf45c0, reinterpret_cast<void*>(NoticeEnter), &originalNotice) && MH_EnableHook(MH_ALL_HOOKS) == MH_OK;
}
DWORD WINAPI Worker(void*) {
    wchar_t name[180]{}; if (!GetEnvironmentVariableW(L"KD_LOCAL_NATIVE_CHANNEL", name, 180)) return 0;
    HANDLE mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name); if (!mapping) return 1;
    status = static_cast<NativeStatus*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, 4096));
    if (!status || status->magic != kMagic || status->version != 1 || status->ownerPid != GetCurrentProcessId()) return 2;
    game = reinterpret_cast<unsigned char*>(GetModuleHandleW(nullptr));
    sdkModule = GetModuleHandleW(L"nexon_api_x64.dll"); if (sdkModule) sdkEpoch = 1;
    analyticsModule = GetModuleHandleW(L"nexonanalytics64.dll"); if (analyticsModule) analyticsEpoch = 1;
    cryptoModule = GetModuleHandleW(L"bcrypt.dll");
    auto registerNotify = reinterpret_cast<Notify>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "LdrRegisterDllNotification"));
    if (!registerNotify || MH_Initialize() != MH_OK || registerNotify(0, ModuleChanged, nullptr, &notificationCookie) != 0) { Fail(100); return 3; }
    for (;;) {
        if (status->error) return 4;
        if (sdkEpoch != doneSdk) {
            auto module = const_cast<void*>(sdkModule); LONG epoch = sdkEpoch;
            if (module && InstallSdk(module)) InterlockedOr(&status->flags, Sdk); else Fail(101);
            InterlockedExchange(&doneSdk, epoch);
        }
        if (analyticsEpoch != doneAnalytics) {
            auto module = const_cast<void*>(analyticsModule); LONG epoch = analyticsEpoch;
            if (module && DirectReplace(Export(module, "initializeClientModule"), reinterpret_cast<void*>(DisableAnalytics), "488bc4574883ec7048c740a8feffffff")) InterlockedOr(&status->flags, Analytics); else Fail(102);
            InterlockedExchange(&doneAnalytics, epoch);
        }
        if (!(status->flags & Crypto) && cryptoModule) { if (InstallCrypto(const_cast<void*>(cryptoModule))) InterlockedOr(&status->flags, Crypto); else Fail(103); }
        if (!(status->flags & Languages) && GameCodeReady()) { if (InstallGameHooks()) InterlockedOr(&status->flags, Languages); else Fail(104); }
        if (!(status->flags & Presets) && status->logins && HexMatch(game + 0x1e27e40, "488974241048897c2418554154415541")) { if (InstallPreset()) InterlockedOr(&status->flags, Presets); else Fail(105); }
        if (!(status->flags & Notice) && status->command && HexMatch(game + 0x1bf45c0, "48895c24185556574154415541564157") && HexMatch(game + 0x1bc9bca, "e8f1a90200")) { if (InstallNotice()) InterlockedOr(&status->flags, Notice); else Fail(106); }
        if (!status->error) InterlockedCompareExchange(&status->ready, 1, 0);
        Sleep(1);
    }
}
}

extern "C" void HookEnter(unsigned kind, void* self, void* caller, void** returnSlot) {
    if (inCallback) return;
    inCallback = true;
    try {
        if (kind == 1) ExtendLanguages(self);
        else if (depth < 32) {
            bool rewrite = kind == 0 || (kind == 2 && caller == game + 0x1b959b6);
            bool restore = false;
            if (kind == 3 && caller == game + 0x1fd4a79) {
                AcquireSRWLockShared(&presetLock); restore = armed && GetTickCount64() <= expires; ReleaseSRWLockShared(&presetLock);
                rewrite = restore;
            }
            if (rewrite) {
                frames[depth++] = {kind, self, caller, restore};
                *returnSlot = kind == 0 ? reinterpret_cast<void*>(CtorLeave) : kind == 2 ? reinterpret_cast<void*>(PolyglotLeave) : reinterpret_cast<void*>(PresetLeave);
            }
        } else Fail(201);
    } catch (...) { Fail(202); }
    inCallback = false;
}
extern "C" void HookLeave(unsigned kind, unsigned char* saved) {
    // This thunk can only be installed after successfully pushing its frame.
    if (depth == 0 || frames[depth - 1].kind != kind) __fastfail(FAST_FAIL_FATAL_APP_EXIT);
    Frame frame = frames[--depth]; std::memcpy(saved + 0xc8, &frame.caller, sizeof frame.caller);
    inCallback = true;
    try {
        if (kind == 0) ExtendLanguages(frame.self);
        else if (kind == 2) {
            auto value = static_cast<unsigned char*>(frame.self);
            auto ns = *reinterpret_cast<wchar_t**>(value + 0x18);
            if (value[0] == 0 && ns && (wcscmp(ns, L"String") == 0 || wcscmp(ns, L"GOT") == 0)) { value[0x98] = 1; InterlockedIncrement(&status->polyglotChanges); }
        } else if (kind == 3 && frame.restore) {
            AcquireSRWLockExclusive(&presetLock); armed = false; ReleaseSRWLockExclusive(&presetLock);
            if (saved[0xb8] == 0) { saved[0xb8] = 1; InterlockedIncrement(&status->restoredPresets); }
        }
    } catch (...) { Fail(203); }
    inCallback = false;
}
extern "C" int ShouldBlockNotice(void* caller, void* notice) {
    if (caller == game + 0x1bc9bcf && notice && *static_cast<unsigned char*>(notice) == 119) { InterlockedIncrement(&status->blockedNotices); return 1; }
    return 0;
}
#ifndef KD_NATIVE_TEST
BOOL WINAPI DllMain(HINSTANCE, DWORD reason, void*) {
    if (reason == DLL_PROCESS_ATTACH) {
        HANDLE worker = CreateThread(nullptr, 0, Worker, nullptr, 0, nullptr);
        if (!worker) return FALSE;
        CloseHandle(worker);
    }
    return TRUE;
}
#endif
