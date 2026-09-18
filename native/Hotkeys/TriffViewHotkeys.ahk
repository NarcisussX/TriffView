#Requires AutoHotkey v2.0
#SingleInstance Off
#NoTrayIcon
Persistent
ListLines False
KeyHistory 0
SetWinDelay -1
A_MaxHotkeysPerInterval := 10000

; Registration and activation follow EVE-X Preview, Main_Class.ahk at
; b5902250f8189f6b3d345b7ba1ac998de6d6b55b. See EVE-X-LICENSE.txt.
; Client selection, profiles, groups and cycle memory belong exclusively to TriffView.
OnError(FatalError)
if A_Args.Length != 2
    ExitApp(2)
HostWindow := Integer(A_Args[1])
HostPid := Integer(A_Args[2])
HostProcess := DllCall("OpenProcess", "UInt", 0x100000, "Int", 0, "UInt", HostPid, "Ptr")
if !HostProcess
    ExitApp(3)
OnExit((*) => DllCall("CloseHandle", "Ptr", HostProcess))

Generation := 0
Enabled := false
RequireForeground := true
ClientHandles := Map()
Bindings := []
RegisteredNames := Map()
RegisteredNames.CaseSense := false
ActivateHwnd := 0
AlwaysMaximize := false
ActivationBusy := false

; Deliberately normal hotkeys for every modifier combination, as in EVE-X.
; Making this internal key wildcarded would change the synthetic-event/hook path.
for prefix in ["", "^", "!", "#", "+", "+^", "+#", "+!", "^#", "^!", "#!", "^+!", "^+#", "^#!", "+!#", "^+#!"]
    Hotkey(prefix "vkE8", ActivateForegroundWindow, "S P1")

OnMessage(0x004A, Configure)
OnMessage(0x8053, ActivateWindow)
OnMessage(0x8054, Stop)
SetTimer(CheckHost, 1000)
DllCall("PostMessage", "Ptr", HostWindow, "UInt", 0x8051, "Ptr", A_ScriptHwnd, "Ptr", 2)

HotkeyScope(*) {
    global Enabled, RequireForeground, ClientHandles
    return Enabled && (!RequireForeground || ClientHandles.Has(DllCall("GetForegroundWindow", "Ptr")))
}

Configure(sender, packet, *) {
    global Generation, Enabled, RequireForeground, ClientHandles, Bindings, RegisteredNames
    if sender != HostWindow || NumGet(packet, 0, "UPtr") != 0x54564132
        return 0
    size := NumGet(packet, A_PtrSize, "UInt")
    if size < 2 || size > 131072 || Mod(size, 2)
        return 0
    data := StrGet(NumGet(packet, 2 * A_PtrSize, "Ptr"), size // 2 - 1, "UTF-16")
    parts := StrSplit(data, "`t")
    try {
        switch parts[1] {
            case "RESET":
                Enabled := false
                HotIf(HotkeyScope)
                for key in Bindings
                    Hotkey(key, "Off")
                Bindings := []
                RegisteredNames.Clear()
                Generation := Integer(parts[2])
                RequireForeground := Integer(parts[3])
                ClientHandles := Map()
                for handle in StrSplit(parts[4], ",")
                    if handle != ""
                        ClientHandles[Integer(handle)] := true
            case "BIND":
                key := parts[4]
                ; vkE8 is reserved for the EVE-X foreground bridge, including its SC alias.
                if InStr(StrLower(key), "vke8") || InStr(StrLower(key), "vk0xe8") || GetKeyVK(RegExReplace(key, "^[*~$<>^!+#]+")) = 0xE8
                    return 0
                if RegisteredNames.Has(key)
                    return 0
                HotIf(HotkeyScope)
                callback := Trigger.Bind(Integer(parts[2]), Generation, Integer(parts[3]))
                Hotkey(key, callback, "On P1")
                RegisteredNames[key] := true
                Bindings.Push(key)
            case "COMMIT":
                Enabled := Integer(parts[2]) != 0
            default:
                return 0
        }
        return 1
    }
    catch {
        return 0
    }
}

Trigger(id, generation, noRepeat, thisHotkey) {
    if !HotkeyScope()
        return
    DllCall("PostMessage", "Ptr", HostWindow, "UInt", 0x8052, "Ptr", id, "Ptr", generation)
    if noRepeat {
        key := RegExReplace(thisHotkey, "^[*~$<>^!+#]+")
        try KeyWait(key)
    }
}

ActivateWindow(hwnd, maximize, *) {
    global ActivateHwnd, AlwaysMaximize, ActivationBusy
    if ActivationBusy || !Enabled || !ClientHandles.Has(hwnd) || !DllCall("IsWindow", "Ptr", hwnd)
        return 0
    ActivationBusy := true
    try {
        ActivateHwnd := hwnd
        AlwaysMaximize := maximize != 0
        if DllCall("GetForegroundWindow", "Ptr") = hwnd
            return 1
        if DllCall("IsIconic", "Ptr", hwnd)
            DllCall("ShowWindowAsync", "Ptr", hwnd, "Int", AlwaysMaximize ? 3 : 9)
        else
            SendEvent("{Blind}{vkE8}")
        ; Yield so the P1 vkE8 hotkey can run, then report observed foreground to C#.
        ; No AttachThreadInput, modifier release/re-press, WinActivate or synthetic Alt.
        deadline := A_TickCount + 600
        loop {
            Sleep(10)
            if DllCall("GetForegroundWindow", "Ptr") = hwnd
                return 1
        } until A_TickCount >= deadline
        return 0
    }
    finally {
        ActivateHwnd := 0
        ActivationBusy := false
    }
}

ActivateForegroundWindow(*) {
    if !ActivateHwnd
        return
    if !DllCall("SetForegroundWindow", "Ptr", ActivateHwnd)
        DllCall("SetForegroundWindow", "Ptr", ActivateHwnd)
    if AlwaysMaximize && WinGetMinMax("ahk_id " ActivateHwnd) = 0
        DllCall("ShowWindowAsync", "Ptr", ActivateHwnd, "Int", 3)
}

CheckHost(*) {
    if DllCall("WaitForSingleObject", "Ptr", HostProcess, "UInt", 0) != 0x102 || !DllCall("IsWindow", "Ptr", HostWindow)
        ExitApp()
}

Stop(sender, *) {
    if sender = HostWindow
        ExitApp()
}

FatalError(error, *) {
    FileAppend(error.Message "`n" error.Extra, "**", "UTF-8")
    ExitApp(1)
}
