namespace YellowInsideLib;

// ──── 디시콘 이미지 자동 전송 (드래그앤드롭 시뮬레이션) ────────────────────────
static class DcconSender
{
    public static async Task SendMultipleDcconsAsync(IntPtr chatWindowHandle, IEnumerable<string> filePaths, Action<string>? log = null)
    {
        log?.Invoke($"[DCCON] 다중 디시콘 전송 시작: 0x{chatWindowHandle:X8}");

        var validFullPaths = new List<string>();
        foreach (var filePath in filePaths)
        {
            if (!File.Exists(filePath))
            {
                log?.Invoke($"[ERROR] 디시콘 이미지 파일이 없습니다: {filePath}");
                continue;
            }
            validFullPaths.Add(Path.GetFullPath(filePath));
        }

        if (validFullPaths.Count == 0)
        {
            log?.Invoke("[ERROR] 전송할 유효한 디시콘 파일이 없습니다");
            return;
        }

        await Task.Run(() =>
        {
            try
            {
                Win32.ChangeWindowMessageFilter(Win32.WM_DROPFILES, Win32.MSGFLT_ADD);
                Win32.ChangeWindowMessageFilter(Win32.WM_COPYGLOBALDATA, Win32.MSGFLT_ADD);

                IntPtr dropFilesHandle = BuildDropFilesMemoryMultiple(validFullPaths);
                if (dropFilesHandle == IntPtr.Zero)
                {
                    log?.Invoke("[ERROR] DROPFILES 메모리 할당 실패");
                    return;
                }

                if (!Win32.PostMessage(chatWindowHandle, Win32.WM_DROPFILES, dropFilesHandle, IntPtr.Zero))
                {
                    Win32.GlobalFree(dropFilesHandle);
                    log?.Invoke("[ERROR] WM_DROPFILES 전송 실패");
                    return;
                }

                log?.Invoke($"[DCCON] ✓ 다중 디시콘 전송 완료: {validFullPaths.Count}개");
            }
            catch (Exception exception)
            {
                log?.Invoke($"[ERROR] 다중 디시콘 전송 실패: {exception.Message}");
            }
        });
    }

    public static async Task SendDcconAsync(IntPtr chatWindowHandle, string filePath, Action<string>? log = null)
    {
        log?.Invoke($"[DCCON] 디시콘 전송 시작: 0x{chatWindowHandle:X8}");

        if (!File.Exists(filePath))
        {
            log?.Invoke($"[ERROR] 디시콘 이미지 파일이 없습니다: {filePath}");
            return;
        }

        string fullPath = Path.GetFullPath(filePath);

        await Task.Run(() =>
        {
            try
            {
                // UIPI 메시지 필터 완화 (WM_DROPFILES 크로스 프로세스 전달 허용)
                Win32.ChangeWindowMessageFilter(Win32.WM_DROPFILES, Win32.MSGFLT_ADD);
                Win32.ChangeWindowMessageFilter(Win32.WM_COPYGLOBALDATA, Win32.MSGFLT_ADD);

                IntPtr dropFilesHandle = BuildDropFilesMemory(fullPath);
                if (dropFilesHandle == IntPtr.Zero)
                {
                    log?.Invoke("[ERROR] DROPFILES 메모리 할당 실패");
                    return;
                }

                if (!Win32.PostMessage(chatWindowHandle, Win32.WM_DROPFILES, dropFilesHandle, IntPtr.Zero))
                {
                    Win32.GlobalFree(dropFilesHandle);
                    log?.Invoke("[ERROR] WM_DROPFILES 전송 실패");
                    return;
                }

                log?.Invoke($"[DCCON] ✓ 디시콘 전송 완료: {fullPath}");
            }
            catch (Exception exception)
            {
                log?.Invoke($"[ERROR] 디시콘 전송 실패: {exception.Message}");
            }
        });
    }

    static unsafe IntPtr BuildDropFilesMemoryMultiple(List<string> filePaths)
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

    static unsafe IntPtr BuildDropFilesMemory(string filePath)
    {
        uint headerSize = (uint)sizeof(Win32.DROPFILES);
        uint filePathByteCount = (uint)((filePath.Length + 1) * sizeof(char));
        uint totalSize = headerSize + filePathByteCount + sizeof(char); // 이중 null 종료

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

            char* filePathDestination = (char*)(lockedPointer + (nint)headerSize);
            filePath.AsSpan().CopyTo(new Span<char>(filePathDestination, filePath.Length));
            // GHND = GMEM_ZEROINIT → 이중 null 종료는 자동 처리
        }
        finally
        {
            Win32.GlobalUnlock(handle);
        }

        return handle;
    }
}
