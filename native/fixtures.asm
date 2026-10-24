option casemap:none
extern testPolyTarget:qword
extern testPresetTarget:qword
extern testNoticeTarget:qword
public CallPolyFixture, PolyFixtureAfter, CallPresetFixture, PresetFixtureAfter, CallNoticeFixture, NoticeFixtureAfter
.code
CallPolyFixture proc
    sub rsp, 28h
    call qword ptr [testPolyTarget]
PolyFixtureAfter::
    add rsp, 28h
    ret
CallPolyFixture endp
CallPresetFixture proc
    sub rsp, 28h
    call qword ptr [testPresetTarget]
PresetFixtureAfter::
    add rsp, 28h
    ret
CallPresetFixture endp
CallNoticeFixture proc
    sub rsp, 28h
    call qword ptr [testNoticeTarget]
NoticeFixtureAfter::
    add rsp, 28h
    ret
CallNoticeFixture endp
end
