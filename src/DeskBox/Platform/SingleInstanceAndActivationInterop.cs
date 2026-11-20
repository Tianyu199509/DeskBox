using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using DeskBox.Services;
using Microsoft.Win32.SafeHandles;

namespace DeskBox.Platform;

/// <summary>
/// Cross-integrity single-instance kernel synchronization, current-user Medium-IL named pipes,
/// and MSIX packaged protocol/launch argument normalization.
/// </summary>
internal static partial class SingleInstanceAndActivationInterop
{
    private const uint EventAllAccess = 0x001F0003;
    private const uint EventModifyAndSynchronize = 0x00100002; // SYNCHRONIZE | EVENT_MODIFY_STATE
    private const uint MutexAllAccess = 0x001F0001;
    private const uint SectionAllAccess = 0x000F001F;
    private const uint PageReadWrite = 0x04;
    private const uint FileMapWrite = 0x0002;
    private const uint FileMapRead = 0x0004;
    private const uint PrimaryOwnerMappingSizeBytes = 4096;
    private const int ErrorAccessDenied = 5;
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeModeByteWaiting = 0x00000000;
    private const uint PipeRejectRemoteClients = 0x00000008;
    private const uint MessageBoxOk = 0x00000000;
    private const uint MessageBoxYesNo = 0x00000004;
    private const uint MessageBoxRetryCancel = 0x00000005;
    private const uint MessageBoxIconQuestion = 0x00000020;
    private const uint MessageBoxIconInformation = 0x00000040;
    private const uint MessageBoxSetForeground = 0x00010000;
    private const uint MessageBoxTopMost = 0x00040000;
    private const uint MessageBoxRight = 0x00080000;
    private const uint MessageBoxRtlReading = 0x00100000;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int DialogResultRetry = 4;
    private const int DialogResultYes = 6;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW",
        SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor,
        uint stringSecurityDescriptorRevision,
        out IntPtr securityDescriptor,
        IntPtr securityDescriptorSize);

    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static partial IntPtr LocalFree(IntPtr hMem);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventExW",
        SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeWaitHandle CreateEventExW(
        ref SecurityAttributes lpEventAttributes,
        string lpName,
        uint dwFlags,
        uint dwDesiredAccess);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateMutexExW",
        SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeWaitHandle CreateMutexExW(
        ref SecurityAttributes lpMutexAttributes,
        string lpName,
        uint dwFlags,
        uint dwDesiredAccess);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW",
        SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafePipeHandle CreateNamedPipeW(
        string pipeName,
        uint openMode,
        uint pipeMode,
        uint maxInstances,
        uint outBufferSize,
        uint inBufferSize,
        uint defaultTimeOut,
        ref SecurityAttributes securityAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileMappingW",
        SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileMappingW(
        IntPtr hFile,
        ref SecurityAttributes lpFileMappingAttributes,
        uint flProtect,
        uint dwMaximumSizeHigh,
        uint dwMaximumSizeLow,
        string lpName);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileMappingW",
        SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileMappingDefaultSecurityW(
        IntPtr hFile,
        IntPtr lpFileMappingAttributes,
        uint flProtect,
        uint dwMaximumSizeHigh,
        uint dwMaximumSizeLow,
        string lpName);

    [LibraryImport("kernel32.dll", EntryPoint = "OpenFileMappingW",
        SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle OpenFileMappingW(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        string lpName);

    [LibraryImport("kernel32.dll", EntryPoint = "MapViewOfFile", SetLastError = true)]
    private static partial IntPtr MapViewOfFile(
        SafeFileHandle hFileMappingObject,
        uint dwDesiredAccess,
        uint dwFileOffsetHigh,
        uint dwFileOffsetLow,
        nuint dwNumberOfBytesToMap);

    [LibraryImport("kernel32.dll", EntryPoint = "UnmapViewOfFile", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW",
        SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(
        IntPtr hWnd,
        string lpText,
        string lpCaption,
        uint uType);

    [LibraryImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    private static partial IntPtr OpenProcess(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwProcessId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(
        IntPtr hProcess,
        uint dwFlags,
        char* lpExeName,
        ref uint lpdwSize);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    public static string? TryGetPackagedProtocolUri(Action<string>? log = null)
    {
        if (!AppDistributionService.Current.IsPackaged)
        {
            return null;
        }

        try
        {
            var activatedArgs = Windows.ApplicationModel.AppInstance.GetActivatedEventArgs();
            if (activatedArgs is Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocolArgs &&
                protocolArgs.Uri is { } uri)
            {
                return uri.AbsoluteUri;
            }

            if (activatedArgs is { Kind: Windows.ApplicationModel.Activation.ActivationKind.File } &&
                activatedArgs is Windows.ApplicationModel.Activation.IFileActivatedEventArgs fileArgs &&
                fileArgs.Files.Count > 0 &&
                fileArgs.Files[0] is { Path: { Length: > 0 } firstFilePath })
            {
                return DeskBoxProtocolActivationService.BuildImportWidgetFileUri(firstFilePath) ?? firstFilePath;
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Failed to inspect packaged protocol activation: {ex.Message}");
        }

        return null;
    }

    public static string[] GetEffectiveLaunchCommandLineArgs(
        string[] commandLineArgs,
        string? packagedLaunchArguments)
    {
        ArgumentNullException.ThrowIfNull(commandLineArgs);
        if (string.IsNullOrWhiteSpace(packagedLaunchArguments))
        {
            return commandLineArgs;
        }

        List<string> extraTokens = SplitCommandLineTokens(packagedLaunchArguments);
        if (extraTokens.Count == 0)
        {
            return commandLineArgs;
        }

        // Avoid duplicating tokens when the OS host already mirrored packaged launch
        // arguments onto the Win32 process command line.
        if (ContainsTokenSubsequence(commandLineArgs, extraTokens))
        {
            return commandLineArgs;
        }

        var merged = new string[commandLineArgs.Length + extraTokens.Count];
        Array.Copy(commandLineArgs, merged, commandLineArgs.Length);
        for (int i = 0; i < extraTokens.Count; i++)
        {
            merged[commandLineArgs.Length + i] = extraTokens[i];
        }

        return merged;
    }

    public static List<string> SplitCommandLineTokens(string? rawArguments)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(rawArguments))
        {
            return tokens;
        }

        var current = new StringBuilder();
        bool inQuotes = false;
        bool tokenStarted = false;

        for (int i = 0; i < rawArguments.Length; i++)
        {
            char ch = rawArguments[i];
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                tokenStarted = true;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (tokenStarted)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    tokenStarted = false;
                }

                continue;
            }

            current.Append(ch);
            tokenStarted = true;
        }

        if (tokenStarted)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    public static EventWaitHandle? CreateOrOpenActivationEvent(
        string eventName,
        Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        string? sid = TryGetCurrentUserSidString();
        if (!string.IsNullOrEmpty(sid) &&
            ConvertStringSecurityDescriptorToSecurityDescriptorW(
                $"D:(A;;0x{EventAllAccess:X8};;;{sid})S:(ML;;NW;;;ME)",
                1,
                out IntPtr descriptor,
                IntPtr.Zero))
        {
            try
            {
                var attributes = new SecurityAttributes
                {
                    Length = (uint)Marshal.SizeOf<SecurityAttributes>(),
                    SecurityDescriptor = descriptor,
                    InheritHandle = 0,
                };

                using SafeWaitHandle nativeEvent = CreateEventExW(
                    ref attributes,
                    eventName,
                    dwFlags: 0,
                    EventAllAccess);
                if (!nativeEvent.IsInvalid)
                {
                    return new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
                }

                int error = Marshal.GetLastWin32Error();
                if (error == ErrorAccessDenied)
                {
                    using SafeWaitHandle modifyEvent = CreateEventExW(
                        ref attributes,
                        eventName,
                        dwFlags: 0,
                        EventModifyAndSynchronize);
                    if (!modifyEvent.IsInvalid)
                    {
                        var fallback = new EventWaitHandle(false, EventResetMode.AutoReset);
                        fallback.SafeWaitHandle.Dispose();
                        fallback.SafeWaitHandle = new SafeWaitHandle(modifyEvent.DangerousGetHandle(), ownsHandle: true);
                        modifyEvent.SetHandleAsInvalid();
                        return fallback;
                    }

                    log?.Invoke($"[Activation] Activation event '{eventName}' denied at current integrity level (win32={error}).");
                    return null;
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                log?.Invoke($"[Activation] Failed to open existing activation event '{eventName}': {ex.Message}");
                return null;
            }
            finally
            {
                LocalFree(descriptor);
            }
        }

        try
        {
            return new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        }
        catch (UnauthorizedAccessException ex)
        {
            log?.Invoke($"[Activation] Activation event fallback denied for '{eventName}': {ex.Message}");
            return null;
        }
    }

    public static (Mutex? Mutex, bool CreatedNew) CreateOrOpenSingleInstanceMutex(
        string mutexName,
        Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);

        string? sid = TryGetCurrentUserSidString();
        if (!string.IsNullOrEmpty(sid) &&
            ConvertStringSecurityDescriptorToSecurityDescriptorW(
                $"D:(A;;0x{MutexAllAccess:X8};;;{sid})S:(ML;;NW;;;ME)",
                1,
                out IntPtr descriptor,
                IntPtr.Zero))
        {
            try
            {
                var attributes = new SecurityAttributes
                {
                    Length = (uint)Marshal.SizeOf<SecurityAttributes>(),
                    SecurityDescriptor = descriptor,
                    InheritHandle = 0,
                };

                using SafeWaitHandle nativeMutex = CreateMutexExW(
                    ref attributes,
                    mutexName,
                    dwFlags: 0,
                    MutexAllAccess);
                if (!nativeMutex.IsInvalid)
                {
                    var managedMutex = new Mutex(initiallyOwned: false, mutexName);
                    if (TryAcquireMutexImmediate(managedMutex))
                    {
                        return (managedMutex, true);
                    }

                    managedMutex.Dispose();
                    return (null, false);
                }

                int error = Marshal.GetLastWin32Error();
                if (error == ErrorAccessDenied)
                {
                    log?.Invoke($"[Activation] Existing primary mutex '{mutexName}' held at higher integrity; treating as secondary instance.");
                    return (null, false);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                log?.Invoke($"[Activation] Existing primary mutex '{mutexName}' denied access ({ex.Message}); treating as secondary instance.");
                return (null, false);
            }
            finally
            {
                LocalFree(descriptor);
            }
        }

        try
        {
            var fallbackMutex = new Mutex(initiallyOwned: false, mutexName);
            if (TryAcquireMutexImmediate(fallbackMutex))
            {
                return (fallbackMutex, true);
            }

            fallbackMutex.Dispose();
            return (null, false);
        }
        catch (UnauthorizedAccessException ex)
        {
            log?.Invoke($"[Activation] Fallback mutex '{mutexName}' denied access ({ex.Message}); treating as secondary instance.");
            return (null, false);
        }
    }

    public static NamedPipeServerStream? TryCreateMediumIntegrityCurrentUserPipeServer(
        string pipeName,
        int maxInstances = 16,
        int bufferSize = 64 * 1024,
        Action<string>? log = null,
        bool firstInstance = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        string? sid = TryGetCurrentUserSidString();
        if (string.IsNullOrEmpty(sid))
        {
            return null;
        }

        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                $"D:(A;;GWGR;;;{sid})S:(ML;;NW;;;ME)",
                1,
                out IntPtr descriptor,
                IntPtr.Zero))
        {
            return null;
        }

        string fullPipePath = pipeName.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase)
            ? pipeName
            : @"\\.\pipe\" + pipeName;

        try
        {
            var attributes = new SecurityAttributes
            {
                Length = (uint)Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor,
                InheritHandle = 0,
            };

            uint openMode = PipeAccessDuplex | FileFlagOverlapped | (firstInstance ? FileFlagFirstPipeInstance : 0u);
            uint pipeMode = PipeModeByteWaiting | PipeRejectRemoteClients;

            SafePipeHandle handle = CreateNamedPipeW(
                fullPipePath,
                openMode,
                pipeMode,
                maxInstances: (uint)Math.Clamp(maxInstances, 1, 254),
                outBufferSize: (uint)Math.Max(1024, bufferSize),
                inBufferSize: (uint)Math.Max(1024, bufferSize),
                defaultTimeOut: 0,
                ref attributes);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                log?.Invoke($"[Pipe] CreateNamedPipeW failed with win32 error {error} for '{fullPipePath}'.");
                return null;
            }

            return new NamedPipeServerStream(
                PipeDirection.InOut,
                isAsync: true,
                isConnected: false,
                handle);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    /// <summary>
    /// Publishes primary-instance metadata into a session-scoped named shared
    /// memory section accessible across both unpackaged Direct and packaged
    /// Microsoft Store (<c>runFullTrust</c>) processes at Medium or High IL.
    /// The OS reclaims the section automatically when the returned handle closes
    /// or the primary process exits.
    /// </summary>
    public static unsafe SafeFileHandle? TryCreatePrimaryOwnerSharedMapping(
        string mappingName,
        string payloadText,
        Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mappingName);

        byte[] utf8 = Encoding.UTF8.GetBytes(payloadText ?? string.Empty);
        int maxPayloadBytes = (int)PrimaryOwnerMappingSizeBytes - sizeof(int);
        int length = Math.Min(utf8.Length, maxPayloadBytes);

        SafeFileHandle? mapping = null;
        string? sid = TryGetCurrentUserSidString();
        if (!string.IsNullOrEmpty(sid) &&
            ConvertStringSecurityDescriptorToSecurityDescriptorW(
                $"D:(A;;0x{SectionAllAccess:X8};;;{sid})S:(ML;;NW;;;ME)",
                1,
                out IntPtr descriptor,
                IntPtr.Zero))
        {
            try
            {
                var attributes = new SecurityAttributes
                {
                    Length = (uint)Marshal.SizeOf<SecurityAttributes>(),
                    SecurityDescriptor = descriptor,
                    InheritHandle = 0,
                };
                mapping = CreateFileMappingW(
                    InvalidHandleValue,
                    ref attributes,
                    PageReadWrite,
                    0,
                    PrimaryOwnerMappingSizeBytes,
                    mappingName);
            }
            finally
            {
                LocalFree(descriptor);
            }
        }

        if (mapping is null || mapping.IsInvalid)
        {
            mapping?.Dispose();
            mapping = CreateFileMappingDefaultSecurityW(
                InvalidHandleValue,
                IntPtr.Zero,
                PageReadWrite,
                0,
                PrimaryOwnerMappingSizeBytes,
                mappingName);
        }

        if (mapping.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            mapping.Dispose();
            log?.Invoke($"[Activation] Failed to create primary-owner shared mapping '{mappingName}' (win32={error}).");
            return null;
        }

        IntPtr view = MapViewOfFile(mapping, FileMapWrite, 0, 0, PrimaryOwnerMappingSizeBytes);
        if (view == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            mapping.Dispose();
            log?.Invoke($"[Activation] Failed to map primary-owner shared section '{mappingName}' (win32={error}).");
            return null;
        }

        try
        {
            byte* dst = (byte*)view;
            *(int*)dst = length;
            if (length > 0)
            {
                fixed (byte* src = utf8)
                {
                    Buffer.MemoryCopy(src, dst + sizeof(int), maxPayloadBytes, length);
                }
            }
        }
        finally
        {
            UnmapViewOfFile(view);
        }

        return mapping;
    }

    public static unsafe string? TryReadPrimaryOwnerSharedMapping(
        string mappingName,
        Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(mappingName))
        {
            return null;
        }

        using SafeFileHandle mapping = OpenFileMappingW(FileMapRead, false, mappingName);
        if (mapping.IsInvalid)
        {
            return null;
        }

        IntPtr view = MapViewOfFile(mapping, FileMapRead, 0, 0, PrimaryOwnerMappingSizeBytes);
        if (view == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            log?.Invoke($"[Activation] Failed to map primary-owner shared section for read '{mappingName}' (win32={error}).");
            return null;
        }

        try
        {
            byte* src = (byte*)view;
            int length = *(int*)src;
            int maxPayloadBytes = (int)PrimaryOwnerMappingSizeBytes - sizeof(int);
            if (length <= 0 || length > maxPayloadBytes)
            {
                return null;
            }

            return Encoding.UTF8.GetString(new ReadOnlySpan<byte>(src + sizeof(int), length));
        }
        finally
        {
            UnmapViewOfFile(view);
        }
    }

    public static (Mutex? Mutex, bool Acquired) TryAcquireSingleInstanceMutexWithTimeout(
        string mutexName,
        int timeoutMs,
        Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        long deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);

        while (true)
        {
            (Mutex? mutex, bool createdNew) = CreateOrOpenSingleInstanceMutex(mutexName, log);
            if (createdNew && mutex is not null)
            {
                return (mutex, true);
            }

            mutex?.Dispose();
            if (Environment.TickCount64 >= deadline)
            {
                return (null, false);
            }

            Thread.Sleep(150);
        }
    }

    public static bool ShowPreUiConfirmation(
        string title,
        string message,
        bool yesNo = true,
        bool isRtl = false)
    {
        uint flags = yesNo
            ? MessageBoxYesNo | MessageBoxIconQuestion | MessageBoxSetForeground | MessageBoxTopMost
            : MessageBoxOk | MessageBoxIconInformation | MessageBoxSetForeground | MessageBoxTopMost;
        if (isRtl)
        {
            flags |= MessageBoxRight | MessageBoxRtlReading;
        }

        int result = MessageBoxW(IntPtr.Zero, message, title, flags);
        return !yesNo || result == DialogResultYes;
    }

    public static bool ShowPreUiRetryPrompt(
        string title,
        string message,
        bool isRtl = false)
    {
        uint flags = MessageBoxRetryCancel | MessageBoxIconInformation | MessageBoxSetForeground | MessageBoxTopMost;
        if (isRtl)
        {
            flags |= MessageBoxRight | MessageBoxRtlReading;
        }

        return MessageBoxW(IntPtr.Zero, message, title, flags) == DialogResultRetry;
    }

    /// <summary>
    /// Queries a process's full executable image path using
    /// <c>PROCESS_QUERY_LIMITED_INFORMATION</c>, which succeeds across Medium
    /// and High integrity levels (unlike <c>Process.MainModule</c>).
    /// </summary>
    public static unsafe string? TryQueryProcessExecutablePath(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            char* buffer = stackalloc char[1024];
            uint length = 1024;
            if (!QueryFullProcessImageNameW(handle, 0, buffer, ref length) || length == 0)
            {
                return null;
            }

            return new string(buffer, 0, (int)length);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool TryAcquireMutexImmediate(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private static bool ContainsTokenSubsequence(string[] commandLineArgs, List<string> extraTokens)
    {
        if (commandLineArgs.Length <= 1 || extraTokens.Count == 0 || commandLineArgs.Length - 1 < extraTokens.Count)
        {
            return false;
        }

        int maxStart = commandLineArgs.Length - extraTokens.Count;
        for (int start = 1; start <= maxStart; start++)
        {
            bool allMatch = true;
            for (int offset = 0; offset < extraTokens.Count; offset++)
            {
                if (!string.Equals(commandLineArgs[start + offset], extraTokens[offset], StringComparison.Ordinal))
                {
                    allMatch = false;
                    break;
                }
            }

            if (allMatch)
            {
                return true;
            }
        }

        return false;
    }

    private static string? TryGetCurrentUserSidString()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch
        {
            return null;
        }
    }
}
