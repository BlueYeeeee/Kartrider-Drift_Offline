#pragma once
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdint>

// Shared with NativeAdapter.cs. No player profile or authentication data lives here.
struct NativeStatus {
    std::uint32_t magic, version, ownerPid;
    volatile LONG ready, flags, error, command;
    volatile LONG sdkInitializations, tickets, logins, restoredPresets;
    volatile LONG expandedLanguages, polyglotChanges, blockedNotices;
};
constexpr std::uint32_t kMagic = 0x4B444F4E;
enum : LONG { Sdk = 1, Crypto = 2, Languages = 4, Presets = 8, Notice = 16, Analytics = 32 };
extern "C" {
    extern void *originalCtor, *originalLanguage, *originalPolyglot, *originalPreset, *originalNotice;
    void CtorEnter(); void CtorLeave(); void LanguageEnter();
    void PolyglotEnter(); void PolyglotLeave(); void PresetEnter(); void PresetLeave(); void NoticeEnter();
    void HookEnter(unsigned kind, void* self, void* caller, void** returnSlot);
    void HookLeave(unsigned kind, unsigned char* savedRegisters);
    int ShouldBlockNotice(void* caller, void* notice);
}
