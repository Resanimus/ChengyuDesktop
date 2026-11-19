; Chengyu hosted adaptation of the supplied TaskbarGuard 1.4.1.
#Requires AutoHotkey v2.0
#SingleInstance Off
#NoTrayIcon

marker := "ChengyuTaskbar_Clipped"
fadeMarker := "ChengyuTaskbar_Fading"
edgePixels := 10
bars := Map()
keyboardUntil := 0
keyboardSession := false
autoHide := false
nextStateCheck := 0
lastMinimizeAt := 0
lastDesktopFocusAttempt := 0
desktopFocusAttempts := 0
minimizeHook := 0
minimizeCallback := 0
appBarData := Buffer(A_PtrSize = 8 ? 48 : 36, 0)
NumPut("UInt", appBarData.Size, appBarData)
DllCall("user32\SetThreadDpiAwarenessContext", "Ptr", -4, "Ptr")
CoordMode("Mouse", "Screen")

hostProcess := 0
stopEvent := 0
readyEvent := 0
fixtureHwnd := 0
if A_Args.Length < 4 || A_Args[1] != "--host"
    ExitApp(2)
hostProcess := DllCall("kernel32\OpenProcess", "UInt", 0x100000, "Int", 0, "UInt", Integer(A_Args[2]), "Ptr")
stopEvent := DllCall("kernel32\OpenEventW", "UInt", 0x100000, "Int", 0, "Str", A_Args[3], "Ptr")
readyEvent := DllCall("kernel32\OpenEventW", "UInt", 2, "Int", 0, "Str", A_Args[4], "Ptr")
if !hostProcess || !stopEvent || !readyEvent
    ExitApp(3)
if A_Args.Length >= 6 && A_Args[5] = "--fixture"
    fixtureHwnd := Integer(A_Args[6])

OnExit(Shutdown)
if !fixtureHwnd {
    minimizeCallback := CallbackCreate(OnMinimize, , 7)
    minimizeHook := DllCall("user32\SetWinEventHook", "UInt", 0x16, "UInt", 0x16, "Ptr", 0,
        "Ptr", minimizeCallback, "UInt", 0, "UInt", 0, "UInt", 2, "Ptr")
    if !minimizeHook
        throw Error("无法监听窗口最小化", , A_LastError)
}
UpdateTaskbars()
SetTimer(UpdateTaskbars, 15)
DllCall("kernel32\SetEvent", "Ptr", readyEvent)

#HotIf !fixtureHwnd
~*LWin::AllowWinKey()
~*RWin::AllowWinKey()
^!F12::ExitApp()
#HotIf

AllowWinKey() {
    global keyboardUntil, keyboardSession
    keyboardUntil := DllCall("kernel32\GetTickCount64", "UInt64") + 900
    keyboardSession := true
    UpdateTaskbars()
}

UpdateTaskbars() {
    global autoHide, nextStateCheck, bars, keyboardSession, appBarData, hostProcess, stopEvent, fixtureHwnd
    Critical()
    if DllCall("kernel32\WaitForSingleObject", "Ptr", hostProcess, "UInt", 0) = 0
        || DllCall("kernel32\WaitForSingleObject", "Ptr", stopEvent, "UInt", 0) = 0
        ExitApp()
    if fixtureHwnd {
        ; Verification targets only an explicitly supplied test window, never the shell.
        if !DllCall("user32\IsWindow", "Ptr", fixtureHwnd)
            ExitApp()
        state := bars.Get(fixtureHwnd, 0)
        if !state
            bars[fixtureHwnd] := state := TaskbarTransition()
        PresentTaskbar(fixtureHwnd, state, false, 48, 400, 48, DllCall("kernel32\GetTickCount64", "UInt64"))
        return
    }
    now := DllCall("kernel32\GetTickCount64", "UInt64")
    if now >= nextStateCheck {
        autoHide := !!(DllCall("shell32\SHAppBarMessage", "UInt", 4, "Ptr", appBarData, "UPtr") & 1)
        nextStateCheck := now + 500
        if !autoHide {
            RestoreAll()
            bars.Clear()
        }
    }
    if !autoHide
        return

    keyboardSession := KeyboardAllowed(now)
    MouseGetPos(&x, &y)
    currentBars := Map()
    for hwnd in FindTaskbars() {
        state := bars.Get(hwnd, 0)
        if !state
            state := TaskbarTransition()
        currentBars[hwnd] := state
        if !ReadBarGeometry(hwnd, x, y, keyboardSession || state.pointerAllowed,
            &depth, &pointerAllowed, &width, &height) {
            if EndFade(hwnd, state.fading)
                state.fading := false
            RestoreTaskbar(hwnd, state.lastSlice)
            continue
        }
        state.pointerAllowed := pointerAllowed
        if ReturnDesktopFocus(hwnd, keyboardSession || pointerAllowed, now) {
            state.desktopResetAt := now
            state.sawResetExpansion := depth > 4
        }
        if keyboardSession
            state.desktopResetAt := 0
        PresentTaskbar(hwnd, state, keyboardSession || pointerAllowed, depth, width, height, now)
    }
    bars := currentBars
}

PresentTaskbar(hwnd, state, allowed, depth, width, height, now) {
    collapsed := depth <= 4
    if state.desktopResetAt {
        state.sawResetExpansion := state.sawResetExpansion || !collapsed
        ready := collapsed && (state.sawResetExpansion || now - state.desktopResetAt >= 96)
        if !ready && !allowed && now - state.desktopResetAt < 1100 {
            masked := ChangeMask(hwnd, 0, width, height, state.lastSlice)
            state.maskedExpansion := masked && !collapsed
            state.revealAt := 0
            if masked
                state.lastSlice := 0
            return
        }
        state.desktopResetAt := 0
    }
    if state.ShouldClip(allowed, collapsed, now, depth) {
        masked := ChangeMask(hwnd, 0, width, height, state.lastSlice)
        state.maskedExpansion := masked && !collapsed
        state.revealAt := 0
        if masked && EndFade(hwnd, state.fading)
            state.fading := false
        if masked || (state.lastSlice && RestoreTaskbar(hwnd, state.lastSlice))
            state.lastSlice := 0
        return
    }
    ; Only animate a bar that was already expanded before input arrived.
    if state.maskedExpansion {
        state.maskedExpansion := false
        if allowed && !collapsed {
            if BeginFade(hwnd) {
                state.fading := true
                state.revealAt := now
                return
            }
        }
    }
    if state.revealAt && state.fading && now - state.revealAt < 145 {
        remaining := 1 - (now - state.revealAt) / 145
        if ApplyFade(hwnd, Round(255 * (1 - remaining ** 2)))
            return
    }
    state.revealAt := 0
    if EndFade(hwnd, state.fading)
        state.fading := false
    if RestoreTaskbar(hwnd, state.lastSlice)
        state.lastSlice := 0
}

class TaskbarTransition {
    pointerAllowed := false
    maskedExpansion := false
    revealAt := 0
    lastSlice := 0
    fading := false
    released := false
    leaveAt := 0
    collapsedAt := 0
    lastDepth := -1
    desktopResetAt := 0
    sawResetExpansion := false

    ShouldClip(allow, collapsed, now, depth := -1) {
        slidingAway := depth >= 0 && this.lastDepth >= 0 && depth < this.lastDepth
        this.lastDepth := depth
        if allow {
            this.released := true
            this.leaveAt := 0
            this.collapsedAt := 0
            return false
        }
        if !this.released
            return true
        if !this.leaveAt
            this.leaveAt := now
        ; An active native slide gets time to finish even after the initial deadline.
        if slidingAway
            this.leaveAt := now
        ; Wait for the native hide. Mask a new reveal as soon as it starts.
        if collapsed && !this.collapsedAt
            this.collapsedAt := now
        if this.collapsedAt {
            if collapsed && now - this.collapsedAt < 96
                return false
        } else if now - this.leaveAt < 560 {
            return false
        }
        this.released := false
        return true
    }
}

OnMinimize(hook, event, hwnd, obj, child, thread, eventTime) {
    global lastMinimizeAt, desktopFocusAttempts
    if !hwnd
        return
    lastMinimizeAt := DllCall("kernel32\GetTickCount64", "UInt64")
    desktopFocusAttempts := 0
}

ReturnDesktopFocus(taskbar, userIsInteracting, now) {
    global lastMinimizeAt, lastDesktopFocusAttempt, desktopFocusAttempts
    if !lastMinimizeAt || userIsInteracting || now - lastMinimizeAt > 2000
        || desktopFocusAttempts >= 2 || now - lastDesktopFocusAttempt < 100
        return
    if DllCall("user32\GetForegroundWindow", "Ptr") != taskbar
        return
    desktop := DllCall("user32\GetShellWindow", "Ptr")
    if !desktop
        return
    desktopFocusAttempts += 1
    lastDesktopFocusAttempt := now
    ; The final minimized app can leave the taskbar active and expanded on the desktop.
    try WinActivate(desktop)
    if DllCall("user32\GetForegroundWindow", "Ptr") = desktop {
        lastMinimizeAt := 0
        return true
    }
    return false
}

KeyboardAllowed(now) {
    global keyboardUntil, keyboardSession
    if GetKeyState("LWin", "P") || GetKeyState("RWin", "P") || now < keyboardUntil
        return true
    if keyboardSession && (hwnd := DllCall("user32\GetForegroundWindow", "Ptr")) {
        try return WinGetProcessName(hwnd) ~= "i)^(StartMenuExperienceHost|SearchHost)\.exe$"
    }
    return false
}

ReadBarGeometry(hwnd, x, y, wasAllowed, &depth, &pointerAllowed, &width, &height) {
    global edgePixels
    static info := Buffer(40, 0), rect := Buffer(16)
    monitor := DllCall("user32\MonitorFromWindow", "Ptr", hwnd, "UInt", 2, "Ptr")
    NumPut("UInt", info.Size, info)
    if !monitor || !DllCall("user32\GetMonitorInfoW", "Ptr", monitor, "Ptr", info)
        || !DllCall("user32\GetWindowRect", "Ptr", hwnd, "Ptr", rect)
        return false
    left := NumGet(rect, 0, "Int"), top := NumGet(rect, 4, "Int")
    right := NumGet(rect, 8, "Int"), bottom := NumGet(rect, 12, "Int")
    width := right - left, height := bottom - top
    edge := NumGet(info, 16, "Int")
    depth := Max(0, Min(bottom, edge) - top)
    onMonitor := x >= NumGet(info, 4, "Int") && x < NumGet(info, 12, "Int")
        && y >= NumGet(info, 8, "Int") && y < edge
    overBar := x >= left && x < right && y >= top && y < bottom
    pointerAllowed := onMonitor && (y >= edge - edgePixels || (wasAllowed && overBar))
    return width > 0 && height > 0
}

FindTaskbars() {
    global fixtureHwnd
    if fixtureHwnd
        return [fixtureHwnd]
    found := []
    for barClass in ["Shell_TrayWnd", "Shell_SecondaryTrayWnd"] {
        hwnd := 0
        while hwnd := DllCall("user32\FindWindowExW", "Ptr", 0, "Ptr", hwnd,
            "Str", barClass, "Ptr", 0, "Ptr")
            found.Push(hwnd)
    }
    return found
}

ChangeMask(hwnd, clipTop, width, height, lastSlice) {
    global marker
    region := DllCall("gdi32\CreateRectRgn", "Int", 0, "Int", 0, "Int", 0, "Int", 0, "Ptr")
    if !region
        return false
    try {
        kind := DllCall("user32\GetWindowRgn", "Ptr", hwnd, "Ptr", region)
        if kind = 1 && clipTop = 0
            return true
        if kind = 0 && clipTop > 0
            return false
        token := DllCall("user32\GetPropW", "Ptr", hwnd, "Str", marker, "Ptr")
        if kind > 1 && !MatchesSlice(region, kind, token, lastSlice)
            return false
        if kind = 2 && lastSlice > 0 && lastSlice = clipTop
            return true
        if !DllCall("gdi32\SetRectRgn", "Ptr", region, "Int", 0,
            "Int", clipTop, "Int", clipTop ? width : 0, "Int", clipTop ? height : 0)
            return false
        if !DllCall("user32\SetPropW", "Ptr", hwnd, "Str", marker, "Ptr", clipTop + 1)
            return false
        if DllCall("user32\SetWindowRgn", "Ptr", hwnd, "Ptr", region, "Int", 1) {
            region := 0  ; Windows now owns the region.
            return true
        }
        if token
            DllCall("user32\SetPropW", "Ptr", hwnd, "Str", marker, "Ptr", token)
        else
            DllCall("user32\RemovePropW", "Ptr", hwnd, "Str", marker, "Ptr")
        return false
    } finally {
        if region
            DllCall("gdi32\DeleteObject", "Ptr", region)
    }
}

MatchesSlice(region, kind, token, lastSlice, recoveryHwnd := 0) {
    static box := Buffer(16), rect := Buffer(16)
    if kind != 2 || !DllCall("gdi32\GetRgnBox", "Ptr", region, "Ptr", box)
        return false
    top := NumGet(box, 4, "Int")
    if NumGet(box, 0, "Int") != 0 || top <= 0
        return false
    if top = token - 1 || top = lastSlice
        return true
    ; The explicit recovery action can also repair an unmarked full-width wipe.
    if recoveryHwnd && DllCall("user32\GetWindowRect", "Ptr", recoveryHwnd, "Ptr", rect)
        return NumGet(box, 8, "Int") = NumGet(rect, 8, "Int") - NumGet(rect, 0, "Int")
            && NumGet(box, 12, "Int") = NumGet(rect, 12, "Int") - NumGet(rect, 4, "Int")
    return false
}

RestoreTaskbar(hwnd, lastSlice := 0, explicitRecovery := false) {
    global marker
    region := DllCall("gdi32\CreateRectRgn", "Int", 0, "Int", 0, "Int", 0, "Int", 0, "Ptr")
    if !region
        return false
    try {
        kind := DllCall("user32\GetWindowRgn", "Ptr", hwnd, "Ptr", region)
        token := DllCall("user32\GetPropW", "Ptr", hwnd, "Str", marker, "Ptr")
        ownSlice := MatchesSlice(region, kind, token, lastSlice, explicitRecovery ? hwnd : 0)
        if (kind = 1 || ownSlice) && !DllCall("user32\SetWindowRgn", "Ptr", hwnd, "Ptr", 0, "Int", 1)
            return false
        if token
            DllCall("user32\RemovePropW", "Ptr", hwnd, "Str", marker, "Ptr")
        return true
    } finally {
        DllCall("gdi32\DeleteObject", "Ptr", region)
    }
}

RestoreAll(*) {
    global bars
    for hwnd in FindTaskbars() {
        state := bars.Get(hwnd, 0)
        EndFade(hwnd, state ? state.fading : false)
        RestoreTaskbar(hwnd, state ? state.lastSlice : 0)
    }
}

BeginFade(hwnd) {
    global fadeMarker
    try style := WinGetExStyle(hwnd)
    catch
        return false
    if style & 0x80000
        return false
    if !DllCall("user32\SetPropW", "Ptr", hwnd, "Str", fadeMarker, "Ptr", 1)
        return false
    if ApplyFade(hwnd, 0) && RestoreTaskbar(hwnd)
        return true
    EndFade(hwnd, true)
    return false
}

ApplyFade(hwnd, opacity) {
    try {
        WinSetTransparent(opacity, hwnd)
        return true
    } catch {
        return false
    }
}

EndFade(hwnd, knownFade := false) {
    global fadeMarker
    if !knownFade && !DllCall("user32\GetPropW", "Ptr", hwnd, "Str", fadeMarker, "Ptr")
        return true
    try {
        ; Finish fully opaque before removing the temporary layered style.
        WinSetTransparent(255, hwnd)
        WinSetTransparent("Off", hwnd)
        DllCall("user32\RemovePropW", "Ptr", hwnd, "Str", fadeMarker, "Ptr")
        return true
    } catch {
        return false
    }
}

Shutdown(*) {
    global minimizeHook, minimizeCallback, hostProcess, stopEvent, readyEvent
    try RestoreAll()
    finally {
        if minimizeHook
            DllCall("user32\UnhookWinEvent", "Ptr", minimizeHook)
        if minimizeCallback
            CallbackFree(minimizeCallback)
        for handle in [hostProcess, stopEvent, readyEvent]
            if handle
                DllCall("kernel32\CloseHandle", "Ptr", handle)
    }
}
