using System.Runtime.InteropServices;

namespace YellowInsideLib;

// ──── 디시콘 이미지 자동 전송 (드래그앤드롭 시뮬레이션 + 클립보드 폴백) ─────────
static class DcconSender
{
    // ── Public API (하위 호환) ────────────────────────────────────────────────

    public static Task SendDcconAsync(IntPtr chatWindowHandle, string filePath, Action<string>? log = null) =>
        SendDcconAsync(chatWindowHandle, filePath, SendMethod.Auto, new LogSink(InfoLog: log, WarnLog: log, ErrorLog: log));

    public static Task SendMultipleDcconsAsync(IntPtr chatWindowHandle, IEnumerable<string> filePaths, Action<string>? log = null) =>
        SendMultipleDcconsAsync(chatWindowHandle, filePaths, SendMethod.Auto, new LogSink(InfoLog: log, WarnLog: log, ErrorLog: log));

    // ── Public API (SendMethod 지정) ─────────────────────────────────────────

    public static async Task SendDcconAsync(IntPtr chatWindowHandle, string filePath, SendMethod sendMethod, LogSink? logSink = null)
    {
        string fullPath = Path.GetFullPath(filePath);
        logSink?.Info($"[전송] 디시콘 전송 시작 — 대상: 0x{chatWindowHandle:X8}, 방식: {sendMethod}, 파일: {Path.GetFileName(filePath)}, 전체 경로: {fullPath}");

        if (!File.Exists(filePath))
        {
            logSink?.Error($"[전송] 디시콘 이미지 파일 없음 — 경로: {fullPath}");
            return;
        }

        await SendFilesAsync(chatWindowHandle, [fullPath], sendMethod, logSink);
    }

    public static async Task SendMultipleDcconsAsync(IntPtr chatWindowHandle, IEnumerable<string> filePaths, SendMethod sendMethod, LogSink? logSink = null)
    {
        var validFullPaths = new List<string>();
        int totalCount = 0;
        int skippedCount = 0;

        foreach (var filePath in filePaths)
        {
            totalCount++;
            if (!File.Exists(filePath))
            {
                skippedCount++;
                logSink?.Error($"[전송] 디시콘 이미지 파일 없음 — 경로: {Path.GetFullPath(filePath)}");
                continue;
            }
            validFullPaths.Add(Path.GetFullPath(filePath));
        }

        logSink?.Info($"[전송] 다중 디시콘 전송 시작 — 대상: 0x{chatWindowHandle:X8}, 방식: {sendMethod}, 전체: {totalCount}개, 유효: {validFullPaths.Count}개, 누락: {skippedCount}개");

        if (validFullPaths.Count == 0)
        {
            logSink?.Error($"[전송] 전송할 유효한 디시콘 파일 없음 — 입력된 {totalCount}개 파일 모두 존재하지 않음");
            return;
        }

        await SendFilesAsync(chatWindowHandle, validFullPaths, sendMethod, logSink);
    }

    // ── 전송 방식 분기 ───────────────────────────────────────────────────────

    static async Task SendFilesAsync(IntPtr chatWindowHandle, List<string> filePaths, SendMethod sendMethod, LogSink? logSink)
    {
        switch (sendMethod)
        {
            case SendMethod.DropFiles:
                await SendViaDropFilesAsync(chatWindowHandle, filePaths, logSink);
                break;

            case SendMethod.Clipboard:
                await SendViaClipboardAsync(chatWindowHandle, filePaths, logSink);
                break;

            case SendMethod.Auto:
                if (!await SendViaDropFilesAsync(chatWindowHandle, filePaths, logSink))
                {
                    logSink?.Info($"[전송] WM_DROPFILES 실패 → 클립보드 방식으로 재시도 — 대상: 0x{chatWindowHandle:X8}");
                    await SendViaClipboardAsync(chatWindowHandle, filePaths, logSink);
                }
                break;
        }
    }

    // ── WM_DROPFILES 방식 (기존 로직) ────────────────────────────────────────

    static async Task<bool> SendViaDropFilesAsync(IntPtr chatWindowHandle, List<string> filePaths, LogSink? logSink)
    {
        return await Task.Run(() =>
        {
            try
            {
                Win32.ChangeWindowMessageFilter(Win32.WM_DROPFILES, Win32.MSGFLT_ADD);
                Win32.ChangeWindowMessageFilter(Win32.WM_COPYGLOBALDATA, Win32.MSGFLT_ADD);

                IntPtr dropFilesHandle = BuildDropFilesMemory(filePaths);
                if (dropFilesHandle == IntPtr.Zero)
                {
                    logSink?.Error($"[전송] DROPFILES 메모리 할당 실패 — GlobalAlloc 반환값 null, 파일 수: {filePaths.Count}개");
                    return false;
                }

                if (!Win32.PostMessage(chatWindowHandle, Win32.WM_DROPFILES, dropFilesHandle, IntPtr.Zero))
                {
                    int win32Error = Marshal.GetLastWin32Error();
                    Win32.GlobalFree(dropFilesHandle);
                    logSink?.Error($"[전송] WM_DROPFILES 전송 실패 — PostMessage 실패, 대상: 0x{chatWindowHandle:X8} (Win32 error: {win32Error})");
                    return false;
                }

                logSink?.Info($"[전송] ✓ WM_DROPFILES 전송 완료 — {filePaths.Count}개 파일, 대상: 0x{chatWindowHandle:X8}");
                return true;
            }
            catch (Exception exception)
            {
                logSink?.Error($"[전송] WM_DROPFILES 전송 중 예외 발생 — 대상: 0x{chatWindowHandle:X8}, {exception.GetType().Name}: {exception.Message}");
                return false;
            }
        });
    }

    // ── 클립보드 방식 (CF_HDROP + Ctrl+V + Enter) ────────────────────────────

    static async Task SendViaClipboardAsync(IntPtr chatWindowHandle, List<string> filePaths, LogSink? logSink)
    {
        await Task.Run(() =>
        {
            try
            {
                logSink?.Info($"[전송] 클립보드 방식 전송 시도 — 대상: 0x{chatWindowHandle:X8}, {filePaths.Count}개 파일");

                // 1. 기존 클립보드 내용 백업
                var savedClipboard = SaveClipboard(logSink);

                // 2. DROPFILES 메모리 구성 → 클립보드에 CF_HDROP 설정
                IntPtr dropFilesHandle = BuildDropFilesMemory(filePaths);
                if (dropFilesHandle == IntPtr.Zero)
                {
                    logSink?.Error($"[전송] 클립보드 방식 DROPFILES 메모리 할당 실패 — GlobalAlloc 반환값 null, 파일 수: {filePaths.Count}개");
                    RestoreClipboard(savedClipboard, logSink);
                    return;
                }

                if (!Win32.OpenClipboard(IntPtr.Zero))
                {
                    int win32Error = Marshal.GetLastWin32Error();
                    Win32.GlobalFree(dropFilesHandle);
                    logSink?.Error($"[전송] 클립보드 열기 실패 — OpenClipboard 반환값 false (Win32 error: {win32Error})");
                    RestoreClipboard(savedClipboard, logSink);
                    return;
                }

                Win32.EmptyClipboard();

                if (Win32.SetClipboardData(Win32.CF_HDROP, dropFilesHandle) == IntPtr.Zero)
                {
                    int win32Error = Marshal.GetLastWin32Error();
                    Win32.GlobalFree(dropFilesHandle);
                    Win32.CloseClipboard();
                    logSink?.Error($"[전송] 클립보드 데이터 설정 실패 — SetClipboardData(CF_HDROP) 반환값 null (Win32 error: {win32Error})");
                    RestoreClipboard(savedClipboard, logSink);
                    return;
                }
                // SetClipboardData 성공 → OS가 핸들 소유권을 가져감 (GlobalFree 금지)

                Win32.CloseClipboard();

                // 3. 대상 창 활성화 → 붙여넣기 → 전송
                logSink?.Info($"[전송] 클립보드 설정 완료, 입력 시뮬레이션 시작 — SetForegroundWindow → Ctrl+V → Enter");
                Win32.SetForegroundWindow(chatWindowHandle);
                Thread.Sleep(50);

                SimulateCtrlV();
                Thread.Sleep(50);

                SimulateKeyPress(Win32.VK_RETURN);
                Thread.Sleep(50);

                logSink?.Info($"[전송] ✓ 클립보드 방식 전송 완료 — {filePaths.Count}개 파일, 대상: 0x{chatWindowHandle:X8}");

                // 4. 클립보드 복원
                RestoreClipboard(savedClipboard, logSink);
            }
            catch (Exception exception)
            {
                logSink?.Error($"[전송] 클립보드 전송 중 예외 발생 — 대상: 0x{chatWindowHandle:X8}, {exception.GetType().Name}: {exception.Message}");
            }
        });
    }

    // ── 클립보드 백업 / 복원 ─────────────────────────────────────────────────

    static unsafe List<(uint Format, IntPtr Handle)> SaveClipboard(LogSink? logSink)
    {
        var entries = new List<(uint Format, IntPtr Handle)>();
        if (!Win32.OpenClipboard(IntPtr.Zero)) return entries;

        try
        {
            uint format = 0;
            while ((format = Win32.EnumClipboardFormats(format)) != 0)
            {
                try
                {
                    IntPtr data = Win32.GetClipboardData(format);
                    if (data == IntPtr.Zero) continue;

                    nuint size = Win32.GlobalSize(data);
                    if (size == 0) continue;

                    IntPtr sourcePointer = Win32.GlobalLock(data);
                    if (sourcePointer == IntPtr.Zero) continue;

                    try
                    {
                        IntPtr copy = Win32.GlobalAlloc(Win32.GHND, size);
                        if (copy == IntPtr.Zero) continue;

                        IntPtr destinationPointer = Win32.GlobalLock(copy);
                        if (destinationPointer == IntPtr.Zero)
                        {
                            Win32.GlobalFree(copy);
                            continue;
                        }

                        try
                        {
                            Buffer.MemoryCopy(
                                sourcePointer.ToPointer(), destinationPointer.ToPointer(),
                                (long)size, (long)size);
                            entries.Add((format, copy));
                        }
                        finally
                        {
                            Win32.GlobalUnlock(copy);
                        }
                    }
                    finally
                    {
                        Win32.GlobalUnlock(data);
                    }
                }
                catch
                {
                    // GDI 핸들 등 HGLOBAL이 아닌 포맷은 건너뜀
                }
            }
        }
        finally
        {
            Win32.CloseClipboard();
        }

        logSink?.Info($"[전송] 클립보드 백업 완료 — {entries.Count}개 포맷 저장");
        return entries;
    }

    static void RestoreClipboard(List<(uint Format, IntPtr Handle)> entries, LogSink? logSink)
    {
        if (entries.Count == 0) return;

        if (!Win32.OpenClipboard(IntPtr.Zero))
        {
            int win32Error = Marshal.GetLastWin32Error();
            logSink?.Warn($"[전송] 클립보드 복원 실패 — OpenClipboard 반환값 false (Win32 error: {win32Error}), 백업 {entries.Count}개 포맷 메모리 해제");
            foreach (var (_, handle) in entries) Win32.GlobalFree(handle);
            return;
        }

        try
        {
            Win32.EmptyClipboard();
            foreach (var (format, handle) in entries)
            {
                if (Win32.SetClipboardData(format, handle) == IntPtr.Zero)
                    Win32.GlobalFree(handle);
                // SetClipboardData 성공 시 OS가 핸들 소유권을 가져감
            }
        }
        finally
        {
            Win32.CloseClipboard();
        }

        logSink?.Info($"[전송] 클립보드 복원 완료 — {entries.Count}개 포맷");
    }

    // ── 입력 시뮬레이션 ──────────────────────────────────────────────────────

    static unsafe void SimulateCtrlV()
    {
        Win32.INPUT* inputs = stackalloc Win32.INPUT[4];
        inputs[0] = CreateKeyInput(Win32.VK_CONTROL, keyUp: false);
        inputs[1] = CreateKeyInput(Win32.VK_V, keyUp: false);
        inputs[2] = CreateKeyInput(Win32.VK_V, keyUp: true);
        inputs[3] = CreateKeyInput(Win32.VK_CONTROL, keyUp: true);
        Win32.SendInput(4, inputs, sizeof(Win32.INPUT));
    }

    static unsafe void SimulateKeyPress(byte virtualKey)
    {
        Win32.INPUT* inputs = stackalloc Win32.INPUT[2];
        inputs[0] = CreateKeyInput(virtualKey, keyUp: false);
        inputs[1] = CreateKeyInput(virtualKey, keyUp: true);
        Win32.SendInput(2, inputs, sizeof(Win32.INPUT));
    }

    static Win32.INPUT CreateKeyInput(byte virtualKey, bool keyUp) => new()
    {
        Type = Win32.INPUT_KEYBOARD,
        Data = new Win32.InputData
        {
            Keyboard = new Win32.KEYBDINPUT
            {
                VirtualKey = virtualKey,
                Flags = keyUp ? Win32.KEYEVENTF_KEYUP : 0,
            },
        },
    };

    // ── DROPFILES 메모리 구성 ────────────────────────────────────────────────

    static unsafe IntPtr BuildDropFilesMemory(List<string> filePaths)
    {
        uint headerSize = (uint)sizeof(Win32.DROPFILES);

        uint totalPathsByteCount = 0;
        foreach (var path in filePaths)
            totalPathsByteCount += (uint)((path.Length + 1) * sizeof(char));
        totalPathsByteCount += sizeof(char); // 이중 null 종료

        uint totalSize = headerSize + totalPathsByteCount;

        IntPtr handle = Win32.GlobalAlloc(Win32.GHND, totalSize);
        if (handle == IntPtr.Zero) return IntPtr.Zero;

        IntPtr lockedPointer = Win32.GlobalLock(handle);
        if (lockedPointer == IntPtr.Zero)
        {
            Win32.GlobalFree(handle);
            return IntPtr.Zero;
        }

        try
        {
            var dropFiles = (Win32.DROPFILES*)lockedPointer;
            dropFiles->pFiles = headerSize;
            dropFiles->pointX = 0;
            dropFiles->pointY = 0;
            dropFiles->fNC = 0;
            dropFiles->fWide = 1;

            char* destination = (char*)(lockedPointer + (nint)headerSize);
            foreach (var path in filePaths)
            {
                path.AsSpan().CopyTo(new Span<char>(destination, path.Length));
                destination += path.Length;
                *destination = '\0';
                destination++;
            }
            // GHND = GMEM_ZEROINIT → 이중 null 종료는 자동 처리
        }
        finally
        {
            Win32.GlobalUnlock(handle);
        }

        return handle;
    }
}
