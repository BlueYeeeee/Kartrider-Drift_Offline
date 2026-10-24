option casemap:none
extern HookEnter:proc
extern HookLeave:proc
extern ShouldBlockNotice:proc
extern originalCtor:qword
extern originalLanguage:qword
extern originalPolyglot:qword
extern originalPreset:qword
extern originalNotice:qword
public CtorEnter, CtorLeave, LanguageEnter, PolyglotEnter, PolyglotLeave, PresetEnter, PresetLeave, NoticeEnter

; At entry RSP mod 16 = 8. Save volatile state before calling C++ and give it
; a 32-byte shadow area. XMM return/argument values and flags are preserved.
save_state macro
    pushfq
    push rax
    push rcx
    push rdx
    push r8
    push r9
    push r10
    push r11
    sub rsp, 088h
    movdqu [rsp+020h], xmm0
    movdqu [rsp+030h], xmm1
    movdqu [rsp+040h], xmm2
    movdqu [rsp+050h], xmm3
    movdqu [rsp+060h], xmm4
    movdqu [rsp+070h], xmm5
endm
restore_state macro
    movdqu xmm0, [rsp+020h]
    movdqu xmm1, [rsp+030h]
    movdqu xmm2, [rsp+040h]
    movdqu xmm3, [rsp+050h]
    movdqu xmm4, [rsp+060h]
    movdqu xmm5, [rsp+070h]
    add rsp, 088h
    pop r11
    pop r10
    pop r9
    pop r8
    pop rdx
    pop rcx
    pop rax
    popfq
endm
enter_hook macro labelName, kindValue, originalName
labelName proc
    save_state
    mov ecx, kindValue
    mov rdx, [rsp+0B0h]
    mov r8, [rsp+0C8h]
    lea r9, [rsp+0C8h]
    call HookEnter
    restore_state
    jmp qword ptr [originalName]
labelName endp
endm
leave_hook macro labelName, kindValue
labelName proc
    ; The intercepted function already consumed its return slot. Reserve a
    ; new one; HookLeave supplies the real caller without clobbering RAX.
    sub rsp, 8
    save_state
    mov ecx, kindValue
    mov rdx, rsp
    call HookLeave
    restore_state
    ret
labelName endp
endm
.code
enter_hook CtorEnter, 0, originalCtor
leave_hook CtorLeave, 0
enter_hook LanguageEnter, 1, originalLanguage
enter_hook PolyglotEnter, 2, originalPolyglot
leave_hook PolyglotLeave, 2
enter_hook PresetEnter, 3, originalPreset
leave_hook PresetLeave, 3

NoticeEnter proc
    save_state
    mov rcx, [rsp+0C8h]
    mov rdx, [rsp+098h] ; original R9 points at the one-byte notice type
    call ShouldBlockNotice
    test eax, eax
    jnz notice_block
    restore_state
    jmp qword ptr [originalNotice]
notice_block:
    restore_state
    ret
NoticeEnter endp
end
