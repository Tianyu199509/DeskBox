using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class FileOpenEcosystemHardeningTests
{
    private const uint WmSettingChange = 0x001A;
    private const uint WmDisplayChange = 0x007E;

    [Fact]
    public void BuildRefreshPlan_MergesPathExpandsUserVarsAndPreservesProtectedVars()
    {
        var machineVars = new Dictionary<string, RegistryEnvironmentEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["Path"] = new(@"%SystemRoot%\system32;C:\Program Files\Git\cmd;", ExpandVariables: true),
            ["CUDA_PATH"] = new(@"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.6", ExpandVariables: false)
        };
        var userVars = new Dictionary<string, RegistryEnvironmentEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["JAVA_HOME"] = new(@"D:\Jdk\temurin-21", ExpandVariables: false),
            ["Path"] = new(@";%JAVA_HOME%\bin;%USERPROFILE%\.cargo\bin", ExpandVariables: true)
        };
        var volatileVars = new Dictionary<string, RegistryEnvironmentEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["USERPROFILE"] = new(@"C:\Users\tester", ExpandVariables: false)
        };
        var currentProcessVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = @"C:\Windows",
            ["USERPROFILE"] = @"C:\Users\tester",
            ["DESKBOX_DATA_DIR"] = @"D:\CustomDeskBoxData",
            ["Path"] = @"C:\Windows\system32"
        };

        EnvironmentRefreshPlan plan = ProcessEnvironmentSyncService.BuildRefreshPlan(
            machineVars,
            userVars,
            volatileVars,
            currentProcessVars);

        Assert.Equal(@"D:\Jdk\temurin-21", plan.VariablesToSet["JAVA_HOME"]);
        Assert.Equal(
            @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.6",
            plan.VariablesToSet["CUDA_PATH"]);
        Assert.Equal(
            @"C:\Windows\system32;C:\Program Files\Git\cmd;D:\Jdk\temurin-21\bin;C:\Users\tester\.cargo\bin",
            plan.VariablesToSet["Path"]);
        Assert.False(plan.VariablesToSet.ContainsKey("DESKBOX_DATA_DIR"));
        Assert.False(plan.VariablesToSet.ContainsKey("SystemRoot"));
        Assert.DoesNotContain("DESKBOX_DATA_DIR", plan.VariablesToRemove);
        Assert.DoesNotContain("SystemRoot", plan.VariablesToRemove);
    }

    [Fact]
    public void BuildRefreshPlan_ScrubsInheritedLauncherPollutionExceptRegistryConfiguredVars()
    {
        var machineVars = new Dictionary<string, RegistryEnvironmentEntry>(StringComparer.OrdinalIgnoreCase);
        var userVars = new Dictionary<string, RegistryEnvironmentEntry>(StringComparer.OrdinalIgnoreCase)
        {
            // User explicitly configured PYTHONPATH in HKCU\Environment — must be kept!
            ["PYTHONPATH"] = new(@"D:\SharedPythonLibs", ExpandVariables: false)
        };
        var volatileVars = new Dictionary<string, RegistryEnvironmentEntry>(StringComparer.OrdinalIgnoreCase);
        var currentProcessVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ELECTRON_RUN_AS_NODE"] = "1",
            ["__COMPAT_LAYER"] = "RUNASADMIN",
            ["VIRTUAL_ENV"] = @"D:\SomeProject\.venv",
            ["CONDA_PREFIX"] = @"C:\Miniconda3\envs\ml",
            ["PYTHONHOME"] = @"C:\Blender\4.2\python",
            ["PYTHONPATH"] = @"C:\Blender\4.2\scripts\modules",
            ["NODE_OPTIONS"] = "--require C:/vscode/bootloader.js",
            ["OLD_DELETED_SDK"] = @"C:\OldSdk",
            ["DESKBOX_PORTABLE"] = "1"
        };
        var previouslyManaged = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "OLD_DELETED_SDK",
            "PYTHONPATH"
        };

        EnvironmentRefreshPlan plan = ProcessEnvironmentSyncService.BuildRefreshPlan(
            machineVars,
            userVars,
            volatileVars,
            currentProcessVars,
            previouslyManaged);

        Assert.Equal(@"D:\SharedPythonLibs", plan.VariablesToSet["PYTHONPATH"]);
        Assert.DoesNotContain("PYTHONPATH", plan.VariablesToRemove);
        Assert.Contains("ELECTRON_RUN_AS_NODE", plan.VariablesToRemove);
        Assert.Contains("__COMPAT_LAYER", plan.VariablesToRemove);
        Assert.Contains("VIRTUAL_ENV", plan.VariablesToRemove);
        Assert.Contains("CONDA_PREFIX", plan.VariablesToRemove);
        Assert.Contains("PYTHONHOME", plan.VariablesToRemove);
        Assert.Contains("NODE_OPTIONS", plan.VariablesToRemove);
        Assert.Contains("OLD_DELETED_SDK", plan.VariablesToRemove);
        Assert.DoesNotContain("DESKBOX_PORTABLE", plan.VariablesToRemove);
    }

    [Fact]
    public void AcquireChildLaunchContaminationScope_ReferenceCountsOverlappingLaunches()
    {
        string? originalElectron = Environment.GetEnvironmentVariable("ELECTRON_RUN_AS_NODE");
        string? originalCompat = Environment.GetEnvironmentVariable("__COMPAT_LAYER");
        try
        {
            lock (ProcessEnvironmentSyncService.SyncGate)
            {
                Environment.SetEnvironmentVariable("ELECTRON_RUN_AS_NODE", "1");
                Environment.SetEnvironmentVariable("__COMPAT_LAYER", "WINXPSP3");
            }

            IDisposable? scope1 = ProcessEnvironmentSyncService.AcquireChildLaunchContaminationScope();
            IDisposable? scope2 = ProcessEnvironmentSyncService.AcquireChildLaunchContaminationScope();

            Assert.NotNull(scope1);
            Assert.NotNull(scope2);
            Assert.Null(Environment.GetEnvironmentVariable("ELECTRON_RUN_AS_NODE"));
            Assert.Null(Environment.GetEnvironmentVariable("__COMPAT_LAYER"));

            // Ending the first launch while the second is still in UAC / ShellExecuteEx
            // must NOT restore the contamination variables prematurely.
            scope1!.Dispose();
            Assert.Null(Environment.GetEnvironmentVariable("ELECTRON_RUN_AS_NODE"));
            Assert.Null(Environment.GetEnvironmentVariable("__COMPAT_LAYER"));

            scope2!.Dispose();
            Assert.Equal("1", Environment.GetEnvironmentVariable("ELECTRON_RUN_AS_NODE"));
            Assert.Equal("WINXPSP3", Environment.GetEnvironmentVariable("__COMPAT_LAYER"));
        }
        finally
        {
            lock (ProcessEnvironmentSyncService.SyncGate)
            {
                Environment.SetEnvironmentVariable("ELECTRON_RUN_AS_NODE", originalElectron);
                Environment.SetEnvironmentVariable("__COMPAT_LAYER", originalCompat);
            }
        }
    }

    [Fact]
    public void IsEnvironmentSettingChange_ValidatesMessageAtomBoundaryAndString()
    {
        Assert.False(DisplayAreaWatcherService.IsEnvironmentSettingChange(WmSettingChange, IntPtr.Zero));
        Assert.False(DisplayAreaWatcherService.IsEnvironmentSettingChange(WmSettingChange, new IntPtr(0x002F)));

        IntPtr envPtr = Marshal.StringToHGlobalUni("Environment");
        IntPtr lowerEnvPtr = Marshal.StringToHGlobalUni("environment");
        IntPtr colorPtr = Marshal.StringToHGlobalUni("ImmersiveColorSet");
        try
        {
            Assert.True(DisplayAreaWatcherService.IsEnvironmentSettingChange(WmSettingChange, envPtr));
            Assert.True(DisplayAreaWatcherService.IsEnvironmentSettingChange(WmSettingChange, lowerEnvPtr));
            Assert.False(DisplayAreaWatcherService.IsEnvironmentSettingChange(WmDisplayChange, envPtr));
            Assert.False(DisplayAreaWatcherService.IsEnvironmentSettingChange(WmSettingChange, colorPtr));
        }
        finally
        {
            Marshal.FreeHGlobal(envPtr);
            Marshal.FreeHGlobal(lowerEnvPtr);
            Marshal.FreeHGlobal(colorPtr);
        }
    }

    [Fact]
    public void SelectOpenDispatchMode_PackagedProcessKeepsExplorerForDirectExecutables()
    {
        Assert.Equal(
            OpenItemDispatchMode.LocalDefaultVerb,
            FileService.SelectOpenDispatchMode(
                isShellLinkShortcut: false,
                targetKind: ShortcutTargetKind.LocalFileSystem,
                pathToOpen: @"C:\Tools\portable.exe",
                isProcessElevated: false,
                isPackagedProcess: false));

        // MSIX packaged builds keep Explorer dispatch so Centennial VFS/Registry
        // redirection does not leak into child Win32 executables.
        Assert.Equal(
            OpenItemDispatchMode.ShellDispatch,
            FileService.SelectOpenDispatchMode(
                isShellLinkShortcut: false,
                targetKind: ShortcutTargetKind.LocalFileSystem,
                pathToOpen: @"C:\Tools\portable.exe",
                isProcessElevated: false,
                isPackagedProcess: true));
    }

    [Fact]
    public void HasShellOpenAssociation_RecognizesDefaultVerbOnlyClassesAndRejectsUnregistered()
    {
        // .ttf (ttffile -> preview) and .cpl (cplfile -> cplopen) have no
        // explicit "open" verb on Windows 11, only a non-"open" default verb.
        Assert.True(Win32Helper.HasShellOpenAssociation(@"C:\Windows\Fonts\segoeui.ttf"));
        Assert.True(Win32Helper.HasShellOpenAssociation(@"C:\Windows\System32\appwiz.cpl"));
        Assert.False(Win32Helper.HasShellOpenAssociation(@"C:\Temp\sample.deskbox_unregistered_ext_9f8a"));
    }

    [Fact]
    public void ResolveShortcutLaunchDirectory_PreventsInstallDirFallbackWhileKeepingValidStartIn()
    {
        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"DeskBox.ShortcutDirTests-{Guid.NewGuid():N}");
        string appFolder = Path.Combine(tempRoot, "PortableApp");
        string customWorkFolder = Path.Combine(tempRoot, "CustomWorkspace");
        string shortcutFolder = Path.Combine(tempRoot, "Shortcuts");
        Directory.CreateDirectory(appFolder);
        Directory.CreateDirectory(customWorkFolder);
        Directory.CreateDirectory(shortcutFolder);

        string targetExe = Path.Combine(appFolder, "tool.exe");
        string shortcutPath = Path.Combine(shortcutFolder, "tool.lnk");
        File.WriteAllText(targetExe, "stub");
        File.WriteAllText(shortcutPath, "stub");

        try
        {
            // 1. Valid WorkingDirectory inside .lnk -> empty override so CShellLink uses its own folder.
            var validMeta = new ShortcutInfo(
                TargetPath: targetExe,
                Description: string.Empty,
                Arguments: string.Empty,
                WorkingDirectory: customWorkFolder,
                IconLocation: string.Empty,
                IconIndex: 0);
            Assert.Equal(
                string.Empty,
                FileService.ResolveShortcutLaunchDirectory(validMeta, targetExe, shortcutPath));

            // 2. Empty WorkingDirectory inside .lnk -> falls back to target's parent directory.
            var emptyWorkDirMeta = validMeta with { WorkingDirectory = string.Empty };
            Assert.Equal(
                appFolder,
                FileService.ResolveShortcutLaunchDirectory(emptyWorkDirMeta, targetExe, shortcutPath));

            // 3. Deleted WorkingDirectory inside .lnk -> falls back to target's parent directory.
            var deletedWorkDirMeta = validMeta with
            {
                WorkingDirectory = Path.Combine(tempRoot, "DeletedFolder")
            };
            Assert.Equal(
                appFolder,
                FileService.ResolveShortcutLaunchDirectory(deletedWorkDirMeta, targetExe, shortcutPath));

            // 4. Non-local/shell-namespace target with empty WorkingDirectory -> shortcut's own directory.
            var shellNamespaceMeta = new ShortcutInfo(
                TargetPath: string.Empty,
                Description: string.Empty,
                Arguments: string.Empty,
                WorkingDirectory: string.Empty,
                IconLocation: string.Empty,
                IconIndex: 0);
            Assert.Equal(
                shortcutFolder,
                FileService.ResolveShortcutLaunchDirectory(shellNamespaceMeta, string.Empty, shortcutPath));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void TryResolveProgramFilesX86Counterpart_Maps64BitPrefixOnlyOnSegmentBoundary()
    {
        string? programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (string.IsNullOrWhiteSpace(programFilesX86))
        {
            return;
        }

        string raw = @"%ProgramFiles%\LegacyVendor\app.exe";
        string expanded = Environment.ExpandEnvironmentVariables(raw);
        Assert.True(
            ShortcutTargetProbe.TryResolveProgramFilesX86Counterpart(
                raw,
                expanded,
                out string counterpart));
        Assert.Equal(
            Path.Combine(programFilesX86, "LegacyVendor", "app.exe"),
            counterpart,
            ignoreCase: true);

        // Must NOT rewrite sibling folder names that merely start with "Program Files".
        string sibling = @"C:\Program Files Custom\Vendor\app.exe";
        Assert.False(
            ShortcutTargetProbe.TryResolveProgramFilesX86Counterpart(
                sibling,
                sibling,
                out _));
    }

    [Fact]
    public void TryExtractFallbackInternetShortcutUri_ReadsProtocolUrlAndRejectsFileUri()
    {
        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"DeskBox.UrlTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        string steamUrlFile = Path.Combine(tempRoot, "CounterStrike.url");
        string localFileUrl = Path.Combine(tempRoot, "LocalFile.url");
        File.WriteAllText(steamUrlFile, "[InternetShortcut]\r\nURL=steam://rungameid/730\r\n");
        File.WriteAllText(localFileUrl, "[InternetShortcut]\r\nURL=file:///C:/Windows/notepad.exe\r\n");

        try
        {
            Assert.True(
                FileService.TryExtractFallbackInternetShortcutUri(
                    steamUrlFile,
                    out string extractedSteamUri));
            Assert.Equal("steam://rungameid/730", extractedSteamUri);

            Assert.False(
                FileService.TryExtractFallbackInternetShortcutUri(
                    localFileUrl,
                    out _));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void HasRunAsUserFlag_DetectsShellLinkRunAsBitWithoutComActivation()
    {
        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"DeskBox.ShortcutFlags-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        string shortcutPath = Path.Combine(tempRoot, "app.lnk");
        string plainTextPath = Path.Combine(tempRoot, "notes.txt");

        try
        {
            Assert.True(ShellLinkVtableInterop.TryCreateEmptyShortcut(shortcutPath));
            File.WriteAllText(plainTextPath, "not a shell link");

            Assert.False(ShortcutHelper.HasRunAsUserFlag(shortcutPath));
            Assert.False(ShortcutHelper.HasRunAsUserFlag(plainTextPath));
            Assert.False(ShortcutHelper.HasRunAsUserFlag(string.Empty));

            byte[] rawBytes = File.ReadAllBytes(shortcutPath);
            uint currentFlags = BinaryPrimitives.ReadUInt32LittleEndian(rawBytes.AsSpan(20, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(
                rawBytes.AsSpan(20, 4),
                currentFlags | 0x00002000u); // SLDF_RUNAS_USER
            File.WriteAllBytes(shortcutPath, rawBytes);

            Assert.True(ShortcutHelper.HasRunAsUserFlag(shortcutPath));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void BrokenShortcutWorkingDirectoryAndX86Counterpart_ResolveCleanlyAcrossDirectAdminAndRevealPaths()
    {
        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"DeskBox.ShortcutHardening-{Guid.NewGuid():N}");
        string fakePf64 = Path.Combine(tempRoot, "Program Files");
        string fakePf86 = Path.Combine(tempRoot, "Program Files (x86)");
        string x86VendorDir = Path.Combine(fakePf86, "LegacyVendor");
        string deletedWorkDir = Path.Combine(tempRoot, "DeletedStartIn");
        string shortcutDir = Path.Combine(tempRoot, "Shortcuts");
        Directory.CreateDirectory(fakePf64);
        Directory.CreateDirectory(x86VendorDir);
        Directory.CreateDirectory(deletedWorkDir);
        Directory.CreateDirectory(shortcutDir);

        string x86Exe = Path.Combine(x86VendorDir, "legacy.exe");
        File.WriteAllBytes(x86Exe, [0x4D, 0x5A]);
        string shortcutPath = Path.Combine(shortcutDir, "LegacyApp.lnk");

        string? origW6432 = Environment.GetEnvironmentVariable("ProgramW6432");
        string? origPf = Environment.GetEnvironmentVariable("ProgramFiles");
        string? origPf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");

        try
        {
            lock (ProcessEnvironmentSyncService.SyncGate)
            {
                Environment.SetEnvironmentVariable("ProgramW6432", fakePf64);
                Environment.SetEnvironmentVariable("ProgramFiles", fakePf64);
                Environment.SetEnvironmentVariable("ProgramFiles(x86)", fakePf86);

                string raw64BitTarget = @"%ProgramFiles%\LegacyVendor\legacy.exe";
                Helpers.WindowsShellLinkTestOracle.CreateOrUpdateFolderShortcut(
                    shortcutPath,
                    Path.Combine(fakePf64, "LegacyVendor", "legacy.exe"),
                    "Legacy 32-bit tool");

                // Mark shortcut with SLDF_RUNAS_USER and simulate deleted WorkingDirectory.
                byte[] lnkBytes = File.ReadAllBytes(shortcutPath);
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(lnkBytes.AsSpan(20, 4));
                BinaryPrimitives.WriteUInt32LittleEndian(lnkBytes.AsSpan(20, 4), flags | 0x00002000u);
                File.WriteAllBytes(shortcutPath, lnkBytes);
                ShortcutHelper.ReleaseHiddenMetadataCache();

                var validWorkDirMeta = new ShortcutInfo(
                    TargetPath: raw64BitTarget,
                    Description: string.Empty,
                    Arguments: "--portable-mode",
                    WorkingDirectory: x86VendorDir,
                    IconLocation: string.Empty,
                    IconIndex: 0);
                Assert.False(FileService.HasBrokenShortcutWorkingDirectory(validWorkDirMeta));

                Directory.Delete(deletedWorkDir, recursive: true);
                var brokenWorkDirMeta = validWorkDirMeta with
                {
                    WorkingDirectory = deletedWorkDir
                };
                Assert.True(FileService.HasBrokenShortcutWorkingDirectory(brokenWorkDirMeta));

                Assert.True(
                    FileService.TryResolveDirectLocalShortcutTarget(
                        shortcutPath,
                        targetPathHint: null,
                        brokenWorkDirMeta,
                        out string resolvedTarget,
                        out string? storedArgs,
                        out string? verb));
                Assert.Equal(Path.GetFullPath(x86Exe), resolvedTarget, ignoreCase: true);
                Assert.Equal("--portable-mode", storedArgs);
                Assert.Equal("runas", verb);

                // Elevated launch request must also resolve the 32-bit x86 counterpart
                // and fall back from a missing start-in folder to the executable directory.
                Assert.True(
                    ElevatedFileLauncher.TryCreateLaunchRequest(
                        shortcutPath,
                        targetPathHint: null,
                        out ElevatedFileLaunchRequest adminRequest));
                Assert.Equal(Path.GetFullPath(x86Exe), adminRequest.FileName, ignoreCase: true);
                Assert.Equal(Path.GetFullPath(x86VendorDir), adminRequest.WorkingDirectory, ignoreCase: true);

                // Reveal-in-Explorer target resolution must also find the 32-bit x86 binary.
                Assert.Equal(
                    Path.GetFullPath(x86Exe),
                    ShellLaunchService.ResolveShortcutRevealTarget(shortcutPath),
                    ignoreCase: true);
            }
        }
        finally
        {
            lock (ProcessEnvironmentSyncService.SyncGate)
            {
                Environment.SetEnvironmentVariable("ProgramW6432", origW6432);
                Environment.SetEnvironmentVariable("ProgramFiles", origPf);
                Environment.SetEnvironmentVariable("ProgramFiles(x86)", origPf86);
            }

            ShortcutHelper.ReleaseHiddenMetadataCache();
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task OpenItemAsync_NonShortcutItemWithEmptyTargetPath_FallsBackToItemPath()
    {
        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"DeskBox.NonShortcutFallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        string unassociatedFile = Path.Combine(
            tempRoot,
            "payload.deskbox_unregistered_ext_9f8a");
        await File.WriteAllTextAsync(unassociatedFile, "sample");

        try
        {
            var item = new WidgetItem
            {
                Name = Path.GetFileName(unassociatedFile),
                Path = unassociatedFile,
                TargetPath = string.Empty,
                IsFolder = false
            };

            FileService.OpenItemResult result = await FileService.OpenItemAsync(item, IntPtr.Zero);
            Assert.Equal(FileService.OpenItemResult.RequiresOpenWithPicker, result);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void NormalizeVisibleOwnerWindow_RejectsZeroAndDestroyedWindowHandles()
    {
        Assert.Equal(IntPtr.Zero, ShellLaunchService.NormalizeVisibleOwnerWindow(IntPtr.Zero));
        Assert.Equal(IntPtr.Zero, ShellLaunchService.NormalizeVisibleOwnerWindow(new IntPtr(0x0BADF00D)));
    }

    [Fact]
    public void UiAndViewModelLayers_RouteAllShellLaunchesThroughShellLaunchService()
    {
        Assembly deskBoxAssembly = typeof(ShellLaunchService).Assembly;
        var violations = new List<string>();

        foreach (Type type in deskBoxAssembly.GetTypes())
        {
            string fullName = type.FullName ?? string.Empty;
            if (!IsUiOrViewModelType(fullName))
            {
                continue;
            }

            const BindingFlags flags =
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.DeclaredOnly;

            foreach (MethodInfo method in type.GetMethods(flags))
            {
                InspectMethodIlForDirectShellCalls(type, method, violations);
            }

            foreach (ConstructorInfo ctor in type.GetConstructors(flags))
            {
                InspectMethodIlForDirectShellCalls(type, ctor, violations);
            }
        }

        Assert.True(
            violations.Count == 0,
            "Views, Controls, and ViewModels must dispatch shell launches/dialogs via ShellLaunchService (or async launcher facades), never raw Process.Start or synchronous Win32 shell helpers:\n" +
            string.Join("\n", violations));
    }

    private static bool IsUiOrViewModelType(string fullTypeName) =>
        fullTypeName.StartsWith("DeskBox.Views.", StringComparison.Ordinal) ||
        fullTypeName.StartsWith("DeskBox.Controls.", StringComparison.Ordinal) ||
        fullTypeName.StartsWith("DeskBox.ViewModels.", StringComparison.Ordinal);

    private static readonly (OpCode[] SingleByte, OpCode[] TwoByte) s_opCodeTables = BuildOpCodeTables();

    private static (OpCode[] SingleByte, OpCode[] TwoByte) BuildOpCodeTables()
    {
        var singleByte = new OpCode[256];
        var twoByte = new OpCode[256];
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(OpCode))
            {
                continue;
            }

            var op = (OpCode)field.GetValue(null)!;
            ushort value = unchecked((ushort)op.Value);
            if ((value & 0xFF00) == 0xFE00)
            {
                twoByte[value & 0xFF] = op;
            }
            else if (value < 0x100)
            {
                singleByte[value] = op;
            }
        }

        return (singleByte, twoByte);
    }

    private static void InspectMethodIlForDirectShellCalls(
        Type declaringType,
        MethodBase method,
        List<string> violations)
    {
        MethodBody? body;
        try
        {
            body = method.GetMethodBody();
        }
        catch
        {
            return;
        }

        byte[]? il = body?.GetILAsByteArray();
        if (il is not { Length: > 0 })
        {
            return;
        }

        Type[] genericTypeArgs = declaringType.IsGenericType
            ? declaringType.GetGenericArguments()
            : Type.EmptyTypes;
        Type[] genericMethodArgs = method is MethodInfo { IsGenericMethod: true } mi
            ? mi.GetGenericArguments()
            : Type.EmptyTypes;

        int offset = 0;
        while (offset < il.Length)
        {
            byte b = il[offset++];
            OpCode op;
            if (b == 0xFE)
            {
                if (offset >= il.Length)
                {
                    break;
                }

                op = s_opCodeTables.TwoByte[il[offset++]];
            }
            else
            {
                op = s_opCodeTables.SingleByte[b];
            }

            switch (op.OperandType)
            {
                case OperandType.InlineNone:
                    break;

                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    offset += 1;
                    break;

                case OperandType.InlineVar:
                    offset += 2;
                    break;

                case OperandType.InlineMethod:
                    if (offset + 4 <= il.Length)
                    {
                        int metadataToken = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, 4));
                        try
                        {
                            MethodBase? target = declaringType.Module.ResolveMethod(
                                metadataToken,
                                genericTypeArgs,
                                genericMethodArgs);
                            if (target is not null && IsForbiddenDirectShellCall(target))
                            {
                                violations.Add(
                                    $"{declaringType.FullName}.{method.Name} -> {target.DeclaringType?.FullName}.{target.Name}");
                            }
                        }
                        catch
                        {
                        }
                    }

                    offset += 4;
                    break;

                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineI:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR:
                    offset += 4;
                    break;

                case OperandType.InlineI8:
                case OperandType.InlineR:
                    offset += 8;
                    break;

                case OperandType.InlineSwitch:
                    if (offset + 4 > il.Length)
                    {
                        return;
                    }

                    int targetCount = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, 4));
                    offset += 4 + (targetCount * 4);
                    break;

                default:
                    return;
            }
        }
    }

    private static bool IsForbiddenDirectShellCall(MethodBase target)
    {
        string declaringType = target.DeclaringType?.FullName ?? string.Empty;
        string name = target.Name;

        return (declaringType, name) switch
        {
            ("System.Diagnostics.Process", "Start") => true,
            ("Windows.System.LauncherOptions", "set_DisplayApplicationPicker") => true,
            ("DeskBox.Platform.Win32Helper", "OpenFile") => true,
            ("DeskBox.Platform.Win32Helper", "OpenFileOrChooseApp") => true,
            ("DeskBox.Platform.Win32Helper", "ShowInExplorer") => true,
            ("DeskBox.Platform.Win32Helper", "ShowFileProperties") => true,
            ("DeskBox.Helpers.ShellOpenWithHelper", "ShowChooseAppDialog") => true,
            ("DeskBox.Helpers.ShellContextMenuHelper", "ShowProperties") => true,
            ("DeskBox.Helpers.ElevatedFileLauncher", "RunAsAdministrator") => true,
            ("DeskBox.Helpers.ShortcutFileLauncher", "TryLaunchWithFiles") => true,
            _ => false
        };
    }
}
