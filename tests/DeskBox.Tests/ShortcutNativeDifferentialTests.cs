using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using DeskBox.Helpers;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class ShortcutNativeDifferentialTests : IDisposable
{
    private const uint StoredRawFieldMask = 0x1F;
    private const uint DiagnosticFieldMask = 0x05;
    private const uint ShellNamespaceWriteFieldMask = 0x03;
    private const uint ResolvePhaseMask = 0x0F;
    private const uint WritePhaseMask = 0x13;
    private const uint UiResolveFlags = 0x0214;

    private static readonly Lazy<ShortcutNativeModule> s_nativeModule = new(() =>
    {
        string path = Path.Combine(AppContext.BaseDirectory, ShortcutNativeModule.DllName);
        ShortcutNativeLoadResult load = ShortcutNativeModule.Load(path);
        if (!load.Success)
        {
            throw new InvalidOperationException($"{load.Failure}: {load.Detail}");
        }

        return load.Module!;
    });

    private readonly string _tempRoot = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(),
        "deskbox-shortcut-differential",
        Guid.NewGuid().ToString("N"))).FullName;

    private ShortcutNativeModule Native => s_nativeModule.Value;

    [Fact]
    public void BackendPolicy_DefaultsToCSharpButForcesRustForNativeAot()
    {
        Assert.Equal(
            ShortcutBackendMode.CSharp,
            ShortcutBackendPolicy.Resolve(configuredValue: null, isDynamicCodeSupported: true));
        Assert.Equal(
            ShortcutBackendMode.CSharp,
            ShortcutBackendPolicy.Resolve("invalid", isDynamicCodeSupported: true));
        Assert.Equal(
            ShortcutBackendMode.Rust,
            ShortcutBackendPolicy.Resolve(" RuSt ", isDynamicCodeSupported: true));
        Assert.Equal(
            ShortcutBackendMode.Rust,
            ShortcutBackendPolicy.Resolve(configuredValue: null, isDynamicCodeSupported: false));
    }

    [Fact]
    public void DiagnosticCapture_DoesNotTriggerTheDefaultNativeLoader()
    {
        bool loadWasAlreadyCreated = ShortcutNativeModule.IsDefaultLoadCreated;

        ShortcutNativeDiagnosticState diagnostic =
            ShortcutNativeBackend.CaptureDiagnosticState();

        Assert.Equal(loadWasAlreadyCreated, ShortcutNativeModule.IsDefaultLoadCreated);
        Assert.Equal(ShortcutBackendPolicy.Current.ToString(), diagnostic.SelectedBackend);
        Assert.Equal(ShortcutNativeModule.DllName, diagnostic.ModuleName);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, diagnostic.ModuleName);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, diagnostic.ModuleName);
        Assert.True(diagnostic.ModuleExists);
        Assert.Equal("X64", diagnostic.ModuleArchitecture);
        Assert.Matches("^[0-9A-F]{64}$", diagnostic.ModuleSha256 ?? string.Empty);
        Assert.Equal(loadWasAlreadyCreated, diagnostic.LoadAttempted);
        if (!loadWasAlreadyCreated)
        {
            Assert.Equal("NotProbed", diagnostic.LoadState);
            Assert.Null(diagnostic.AbiVersion);
            Assert.Null(diagnostic.Capabilities);
        }
    }

    [Fact]
    public void LoaderReadsCurrentAbiAndAllStage3C2Capabilities()
    {
        Assert.Equal(2u, Native.ProbeAbiVersion());
        Assert.Equal(511ul, Native.ProbeCapabilities());
        Assert.Equal(511ul, Native.Capabilities);
        Assert.NotEqual(0, Native.ModuleHandle);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ShortcutNativeModule.DllName)),
            Native.ModulePath,
            ignoreCase: true);
    }

    [Fact]
    public void LoaderDistinguishesMissingModuleAndMissingExport()
    {
        ShortcutNativeLoadResult missing = ShortcutNativeModule.Load(
            Path.Combine(_tempRoot, "missing.dll"));
        Assert.Equal(ShortcutNativeLoadFailure.MissingModule, missing.Failure);

        ShortcutNativeLoadResult wrongModule = ShortcutNativeModule.Load(
            Path.Combine(Environment.SystemDirectory, "kernel32.dll"));
        Assert.Equal(ShortcutNativeLoadFailure.MissingExport, wrongModule.Failure);
        Assert.Contains("deskbox_native_abi_version", wrongModule.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedAbiLayoutsMatchFrozenX64Contract()
    {
        Assert.Equal(16, Marshal.SizeOf<ShortcutNativeModule.NativeUtf16Buffer>());
        Assert.Equal(144, Marshal.SizeOf<ShortcutNativeModule.NativeReadRequest>());
        Assert.Equal(136, Marshal.SizeOf<ShortcutNativeModule.NativeReadResult>());
        Assert.Equal(192, Marshal.SizeOf<ShortcutNativeModule.NativeResolveRequest>());
        Assert.Equal(16, Marshal.SizeOf<ShortcutNativeModule.NativeUtf16String>());
        Assert.Equal(144, Marshal.SizeOf<ShortcutNativeModule.NativeWriteRequest>());
        Assert.Equal(96, Marshal.SizeOf<ShortcutNativeModule.NativeWriteResult>());
        Assert.Equal(64, Marshal.SizeOf<ShortcutNativeModule.NativeUiResolveRequest>());
        Assert.Equal(64, Marshal.SizeOf<ShortcutNativeModule.NativeUiResolveResult>());
    }

    [Fact]
    public void StoredRaw_NormalUnicodeAndOptionalFieldsMatchCSharpOracle()
    {
        string target = CreateTarget("目标 application.exe");
        string workingDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "工作 目录")).FullName;
        string icon = Path.Combine(_tempRoot, "图标 source.ico");
        File.WriteAllBytes(icon, [0]);
        string link = CreatePathShortcut(
            "正常 Unicode.lnk",
            target,
            "DeskBox 描述 Δ",
            "  --alpha=\"值\"  ",
            workingDirectory,
            icon,
            -7);

        ShortcutInfo csharp = ShortcutHelper.ReadStoredMetadataWithCSharpUncached(link);
        ShortcutNativeCallResult rust = Native.ReadStoredRaw(link);

        Assert.True(rust.Success, rust.Detail);
        Assert.Equal(StoredRawFieldMask, rust.AttemptedFields);
        Assert.Equal(StoredRawFieldMask, rust.SucceededFields);
        Assert.Equal(StoredRawFieldMask, rust.PresentFields);
        AssertShortcutEqual(csharp, rust.Metadata!);
    }

    [Fact]
    public void EffectiveDiagnostic_TrimmingMatchesCSharpOracle()
    {
        string target = CreateTarget("diagnostic.exe");
        string link = CreatePathShortcut(
            "diagnostic.lnk",
            target,
            arguments: "\u2003  --diagnostic=\"值\"  \u3000");

        bool csharpSuccess = DragDropPermissionService.TryReadShortcutWithCSharp(
            link,
            out string csharpTarget,
            out string csharpArguments);
        ShortcutNativeCallResult rust = Native.ReadEffectiveDiagnostic(link);

        Assert.True(csharpSuccess);
        Assert.True(rust.Success, rust.Detail);
        Assert.Equal(DiagnosticFieldMask, rust.AttemptedFields);
        Assert.Equal(csharpTarget, rust.Metadata!.TargetPath);
        Assert.Equal(csharpArguments, rust.Metadata.Arguments);
        Assert.Equal("--diagnostic=\"值\"", rust.Metadata.Arguments);
    }

    [Fact]
    public void LongUnicodeArgumentsMatchBothCSharpOracles()
    {
        string unicodeValue = string.Concat(Enumerable.Repeat("路径🚀", 110));
        string link = CreatePathShortcut(
            "long-unicode.lnk",
            CreateTarget("long-unicode.exe"),
            arguments: $"\u3000{unicodeValue}\u2003");

        ShortcutInfo csharpStored = ShortcutHelper.ReadStoredMetadataWithCSharpUncached(link);
        ShortcutNativeCallResult rustStored = Native.ReadStoredRaw(link);
        Assert.True(rustStored.Success, rustStored.Detail);
        AssertShortcutEqual(csharpStored, rustStored.Metadata!);
        Assert.Equal(1u << 2, rustStored.SourceTruncatedFields);

        bool csharpDiagnosticSuccess = DragDropPermissionService.TryReadShortcutWithCSharp(
            link,
            out string csharpTarget,
            out string csharpArguments);
        ShortcutNativeCallResult rustDiagnostic = Native.ReadEffectiveDiagnostic(link);
        Assert.True(csharpDiagnosticSuccess);
        Assert.True(rustDiagnostic.Success, rustDiagnostic.Detail);
        Assert.Equal(csharpTarget, rustDiagnostic.Metadata!.TargetPath);
        Assert.Equal(csharpArguments, rustDiagnostic.Metadata.Arguments);
        Assert.Equal(unicodeValue, rustDiagnostic.Metadata.Arguments);
        Assert.Equal(0u, rustDiagnostic.SourceTruncatedFields);
    }

    [Theory]
    [InlineData(259)]
    [InlineData(260)]
    [InlineData(261)]
    public void StoredRaw_ArgumentBoundariesMatchCSharpOracle(int length)
    {
        string link = CreatePathShortcut(
            $"stored-{length}.lnk",
            CreateTarget($"stored-{length}.exe"),
            arguments: new string('x', length));

        ShortcutInfo csharp = ShortcutHelper.ReadStoredMetadataWithCSharpUncached(link);
        ShortcutNativeCallResult rust = Native.ReadStoredRaw(link);

        Assert.True(rust.Success, rust.Detail);
        AssertShortcutEqual(csharp, rust.Metadata!);
        Assert.Equal(1u << 2, rust.SourceTruncatedFields);
    }

    [Theory]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(513)]
    public void EffectiveDiagnostic_ArgumentBoundariesMatchCSharpOracle(int length)
    {
        string link = CreatePathShortcut(
            $"diagnostic-{length}.lnk",
            CreateTarget($"diagnostic-{length}.exe"),
            arguments: new string('y', length));

        bool csharpSuccess = DragDropPermissionService.TryReadShortcutWithCSharp(
            link,
            out string csharpTarget,
            out string csharpArguments);
        ShortcutNativeCallResult rust = Native.ReadEffectiveDiagnostic(link);

        Assert.True(csharpSuccess);
        Assert.True(rust.Success, rust.Detail);
        Assert.Equal(csharpTarget, rust.Metadata!.TargetPath);
        Assert.Equal(csharpArguments, rust.Metadata.Arguments);
        Assert.Equal(1u << 2, rust.SourceTruncatedFields);
    }

    [Fact]
    public void StoredRaw_MissingTargetAndEmptyOptionalFieldsMatchCSharpOracle()
    {
        string target = Path.Combine(_tempRoot, "missing", "application.exe");
        string link = CreatePathShortcut("missing-target.lnk", target);

        ShortcutInfo csharp = ShortcutHelper.ReadStoredMetadataWithCSharpUncached(link);
        ShortcutNativeCallResult rust = Native.ReadStoredRaw(link);

        Assert.True(rust.Success, rust.Detail);
        AssertShortcutEqual(csharp, rust.Metadata!);
        Assert.Equal(1u, rust.PresentFields);
    }

    [Theory]
    [InlineData(@"%SystemRoot%\System32\notepad.exe")]
    [InlineData(@"\\localhost\C$\DeskBox-missing-target.exe")]
    public void StoredRaw_SpecialPathFormsMatchCSharpOracle(string target)
    {
        string link = CreatePathShortcut(
            $"special-{Guid.NewGuid():N}.lnk",
            target,
            arguments: "--special");

        ShortcutInfo csharp = ShortcutHelper.ReadStoredMetadataWithCSharpUncached(link);
        ShortcutNativeCallResult rust = Native.ReadStoredRaw(link);

        Assert.True(rust.Success, rust.Detail);
        AssertShortcutEqual(csharp, rust.Metadata!);
        Assert.Equal(target, csharp.TargetPath, ignoreCase: true);
    }

    [Fact]
    public void StoredRaw_RelativePathDataMatchesCSharpOracle()
    {
        string relativeTarget = @"relative\DeskBox-target.exe";
        string fullTarget = CreateTarget(relativeTarget);
        string link = CreateRelativePathShortcut(
            "relative.lnk",
            fullTarget,
            relativeTarget);

        ShortcutInfo csharp = ShortcutHelper.ReadStoredMetadataWithCSharpUncached(link);
        ShortcutNativeCallResult rust = Native.ReadStoredRaw(link);

        Assert.True(rust.Success, rust.Detail);
        AssertShortcutEqual(csharp, rust.Metadata!);
    }

    [Fact]
    public void PidlOnlyShortcutHasTheSameStoredAndDiagnosticOutcome()
    {
        string link = CreatePidlShortcut("pidl-recycle-bin.lnk", "shell:RecycleBinFolder");

        (bool csharpStoredSuccess, ShortcutInfo? csharpStored) = TryReadStoredCSharp(link);
        ShortcutNativeCallResult rustStored = Native.ReadStoredRaw(link);
        Assert.Equal(csharpStoredSuccess, rustStored.Success);
        if (csharpStoredSuccess)
        {
            AssertShortcutEqual(csharpStored!, rustStored.Metadata!);
        }

        bool csharpDiagnosticSuccess = DragDropPermissionService.TryReadShortcutWithCSharp(
            link,
            out string csharpTarget,
            out string csharpArguments);
        ShortcutNativeCallResult rustDiagnostic = Native.ReadEffectiveDiagnostic(link);
        Assert.Equal(csharpDiagnosticSuccess, rustDiagnostic.Success);
        if (csharpDiagnosticSuccess)
        {
            Assert.Equal(csharpTarget, rustDiagnostic.Metadata!.TargetPath);
            Assert.Equal(csharpArguments, rustDiagnostic.Metadata.Arguments);
        }
    }

    [Fact]
    public void CorruptedShortcutFailsInBothBackendsWithoutFieldData()
    {
        string link = Path.Combine(_tempRoot, "corrupted.lnk");
        File.WriteAllText(link, "not a shell link");

        (bool csharpSuccess, _) = TryReadStoredCSharp(link);
        ShortcutNativeCallResult rust = Native.ReadStoredRaw(link);

        Assert.False(csharpSuccess);
        Assert.False(rust.Success);
        Assert.Equal(ShortcutNativeCallFailure.NativeFailure, rust.Failure);
        Assert.Equal(0u, rust.AttemptedFields);
    }

    [Fact]
    public void ResolveNoUi_ValidAndMissingTargetsMatchCSharpOracle()
    {
        string validTarget = CreateTarget("resolve-valid.exe");
        string validLink = CreatePathShortcut(
            "resolve-valid.lnk",
            validTarget,
            arguments: "--resolve-valid");
        AssertResolveEqual(validLink, timeoutMs: 100);

        string missingTarget = Path.Combine(_tempRoot, "missing", "resolve.exe");
        string missingLink = CreatePathShortcut(
            "resolve-missing.lnk",
            missingTarget,
            arguments: "--resolve-missing");
        ShortcutNativeCallResult missingRust = AssertResolveEqual(missingLink, timeoutMs: 1);
        Assert.NotEqual(0, missingRust.ResolveHResult);
    }

    [Fact]
    public void ResolveWithUi_ValidShortcutForwardsOwnerAndFrozenFlags()
    {
        string link = CreatePathShortcut(
            "resolve-with-ui-valid.lnk",
            CreateTarget("resolve-with-ui-valid.exe"),
            arguments: "--resolve-with-ui");
        nint ownerHwnd = GetDesktopWindow();

        Assert.NotEqual(0, ownerHwnd);
        ShortcutNativeUiResolveCallResult result = Native.ResolveWithUi(link, ownerHwnd);

        Assert.True(result.Success, result.Detail);
        Assert.Equal(ResolvePhaseMask, result.AttemptedPhases);
        Assert.Equal(UiResolveFlags, result.ResolveFlags);
        Assert.True(result.ResolveHResult >= 0);
        Assert.True(File.Exists(link));
    }

    [Fact]
    public void ResolveWithUi_CorruptShortcutFailsBeforeResolveWithoutOpeningUi()
    {
        string link = Path.Combine(_tempRoot, "resolve-with-ui-corrupt.lnk");
        File.WriteAllText(link, "not a shell link");

        ShortcutNativeUiResolveCallResult result = Native.ResolveWithUi(link, GetDesktopWindow());

        Assert.False(result.Success);
        Assert.Equal(ShortcutNativeCallFailure.NativeFailure, result.Failure);
        Assert.Equal(0x07u, result.AttemptedPhases);
        Assert.Equal(UiResolveFlags, result.ResolveFlags);
        Assert.True(result.LoadHResult < 0);
        Assert.Equal(ShortcutNativeModule.HResultNotAttempted, result.ResolveHResult);
    }

    [Fact]
    public void ApplicationShortcutUiResolveKeepsLinkAndInvalidatesStoredMetadataCache()
    {
        string firstTarget = CreateTarget("ui-cache-first.exe");
        string secondTarget = CreateTarget("ui-cache-second.exe");
        string link = CreatePathShortcut("ui-cache.lnk", firstTarget);
        Assert.Equal(firstTarget, ShortcutHelper.ReadStoredMetadata(link)?.TargetPath);

        BrokenShortcutResolution resolution =
            ShortcutHelper.ResolveBrokenShortcutWithShellUi(link, GetDesktopWindow());
        CreatePathShortcut("ui-cache.lnk", secondTarget);

        Assert.Equal(BrokenShortcutResolution.ResolvedOrKept, resolution);
        Assert.Equal(secondTarget, ShortcutHelper.ReadStoredMetadata(link)?.TargetPath);
    }

    [Fact]
    public void Write_AllFieldsAndNegativeIconIndexMatchCSharpOracle()
    {
        string target = CreateTarget("write-all target.exe");
        string workingDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "write 工作目录")).FullName;
        string icon = Path.Combine(_tempRoot, "write 图标.ico");
        File.WriteAllBytes(icon, [0]);
        const string description = "写入描述 🚀";
        const string arguments = "  --write=\"值\"  ";
        const int iconIndex = -13;
        string csharpLink = CreatePathShortcut(
            "write-all-csharp.lnk",
            target,
            description,
            arguments,
            workingDirectory,
            icon,
            iconIndex);
        string rustLink = Path.Combine(_tempRoot, "write-all-rust.lnk");
        var metadata = new ShortcutInfo(
            target,
            description,
            arguments,
            workingDirectory,
            icon,
            iconIndex);

        ShortcutNativeWriteCallResult write = Native.WriteShortcut(rustLink, metadata);

        Assert.True(write.Success, write.Detail);
        Assert.Equal(WritePhaseMask, write.AttemptedPhases);
        Assert.Equal(StoredRawFieldMask, write.AttemptedFields);
        Assert.Equal(StoredRawFieldMask, write.SucceededFields);
        Assert.Equal(0, write.SaveHResult);
        Assert.Equal(0, write.TargetHResult);
        Assert.Equal(0, write.DescriptionHResult);
        Assert.Equal(0, write.ArgumentsHResult);
        Assert.Equal(0, write.WorkingDirectoryHResult);
        Assert.Equal(0, write.IconHResult);
        AssertStoredLinksEqual(csharpLink, rustLink);
    }

    [Fact]
    public void Write_FolderAndApplicationShapesMatchProductCSharpOracles()
    {
        string folderTarget = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "folder target")).FullName;
        string csharpFolderLink = Path.Combine(_tempRoot, "folder-csharp.lnk");
        string rustFolderLink = Path.Combine(_tempRoot, "folder-rust.lnk");
        ShortcutHelper.CreateOrUpdateFolderShortcutWithCSharp(
            csharpFolderLink,
            folderTarget,
            "folder description");
        ShortcutNativeWriteCallResult folderWrite = Native.WriteShortcut(
            rustFolderLink,
            new ShortcutInfo(
                folderTarget,
                "folder description",
                string.Empty,
                folderTarget,
                string.Empty,
                0));
        Assert.True(folderWrite.Success, folderWrite.Detail);
        AssertStoredLinksEqual(csharpFolderLink, rustFolderLink);

        string appTarget = CreateTarget("application shape.exe");
        string appWorkingDirectory = Path.GetDirectoryName(appTarget)!;
        string appIcon = Path.Combine(_tempRoot, "application.ico");
        File.WriteAllBytes(appIcon, [0]);
        string csharpAppLink = Path.Combine(_tempRoot, "application-csharp.lnk");
        string rustAppLink = Path.Combine(_tempRoot, "application-rust.lnk");
        DragDropPermissionService.CreateOrUpdateShortcutWithCSharp(
            csharpAppLink,
            appTarget,
            "--application",
            appWorkingDirectory,
            appIcon);
        ShortcutNativeWriteCallResult appWrite = Native.WriteShortcut(
            rustAppLink,
            new ShortcutInfo(
                appTarget,
                string.Empty,
                "--application",
                appWorkingDirectory,
                appIcon,
                0));
        Assert.True(appWrite.Success, appWrite.Detail);
        AssertStoredLinksEqual(csharpAppLink, rustAppLink);
    }

    [Fact]
    public void Write_ShellNamespaceTargetMatchesProductCSharpOracle()
    {
        string csharpLink = Path.Combine(_tempRoot, "shell-namespace-csharp.lnk");
        string rustLink = Path.Combine(_tempRoot, "shell-namespace-rust.lnk");
        const string parsingName = "shell:RecycleBinFolder";
        const string description = "Shell namespace shortcut";
        ShortcutHelper.CreateShellNamespaceShortcutWithCSharp(
            csharpLink,
            parsingName,
            description);

        ShortcutNativeWriteCallResult write =
            Native.WriteShellNamespaceShortcut(
                rustLink,
                parsingName,
                description);

        Assert.True(write.Success, write.Detail);
        Assert.Equal(WritePhaseMask, write.AttemptedPhases);
        Assert.Equal(ShellNamespaceWriteFieldMask, write.AttemptedFields);
        Assert.Equal(ShellNamespaceWriteFieldMask, write.SucceededFields);
        Assert.Equal(0, write.TargetHResult);
        Assert.Equal(0, write.DescriptionHResult);
        AssertStoredLinksEqual(csharpLink, rustLink);
        Assert.Empty(
            ShortcutHelper.ReadStoredMetadataWithCSharpUncached(rustLink).TargetPath);
    }

    [Fact]
    public void Write_OverwriteClearsStaleOptionalFieldsLikeCSharpOracle()
    {
        string oldTarget = CreateTarget("overwrite-old.exe");
        string oldIcon = Path.Combine(_tempRoot, "overwrite-old.ico");
        File.WriteAllBytes(oldIcon, [0]);
        string csharpLink = CreatePathShortcut(
            "overwrite-csharp.lnk",
            oldTarget,
            "old description",
            "--old",
            _tempRoot,
            oldIcon,
            -9);
        string rustLink = CreatePathShortcut(
            "overwrite-rust.lnk",
            oldTarget,
            "old description",
            "--old",
            _tempRoot,
            oldIcon,
            -9);
        string folderTarget = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "overwrite folder")).FullName;

        ShortcutHelper.CreateOrUpdateFolderShortcutWithCSharp(
            csharpLink,
            folderTarget,
            "new description");
        ShortcutNativeWriteCallResult write = Native.WriteShortcut(
            rustLink,
            new ShortcutInfo(
                folderTarget,
                "new description",
                string.Empty,
                folderTarget,
                string.Empty,
                0));

        Assert.True(write.Success, write.Detail);
        AssertStoredLinksEqual(csharpLink, rustLink);
        ShortcutInfo actual = ShortcutHelper.ReadStoredMetadataWithCSharpUncached(rustLink);
        Assert.Equal(string.Empty, actual.Arguments);
        Assert.Equal(string.Empty, actual.IconLocation);
        Assert.Equal(0, actual.IconIndex);
    }

    [Fact]
    public void Write_InvalidInputAndSaveFailureAreDiagnosticWithoutFallback()
    {
        string target = CreateTarget("write-failure.exe");
        ShortcutNativeWriteCallResult invalid = Native.WriteShortcut(
            "invalid\0shortcut.lnk",
            new ShortcutInfo(target, string.Empty, string.Empty, string.Empty, string.Empty, 0));
        Assert.False(invalid.Success);
        Assert.Equal(ShortcutNativeCallFailure.NativeFailure, invalid.Failure);
        Assert.Equal(0u, invalid.AttemptedPhases);

        ShortcutNativeWriteCallResult emptyTarget = Native.WriteShortcut(
            Path.Combine(_tempRoot, "empty-target.lnk"),
            new ShortcutInfo(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 0));
        Assert.False(emptyTarget.Success);
        Assert.Equal(0u, emptyTarget.AttemptedPhases);

        ShortcutNativeWriteCallResult oversizedValue = Native.WriteShortcut(
            Path.Combine(_tempRoot, "oversized-value.lnk"),
            new ShortcutInfo(
                target,
                new string('x', 32_768),
                string.Empty,
                string.Empty,
                string.Empty,
                0));
        Assert.False(oversizedValue.Success);
        Assert.Equal(0u, oversizedValue.AttemptedPhases);

        string missingParent = Path.Combine(_tempRoot, "missing-parent", "write.lnk");
        ShortcutNativeWriteCallResult saveFailure = Native.WriteShortcut(
            missingParent,
            new ShortcutInfo(target, "failure", "--failure", string.Empty, string.Empty, 0));
        Assert.False(saveFailure.Success);
        Assert.Equal(ShortcutNativeCallFailure.NativeFailure, saveFailure.Failure);
        Assert.Equal(WritePhaseMask, saveFailure.AttemptedPhases);
        Assert.Equal(StoredRawFieldMask, saveFailure.SucceededFields);
        Assert.NotEqual(0, saveFailure.SaveHResult);
        Assert.False(File.Exists(missingParent));
    }

    [Fact]
    public void Write_WorksOnStaAndMtaThreads()
    {
        string target = CreateTarget("write-apartments.exe");
        foreach (ApartmentState apartment in new[] { ApartmentState.STA, ApartmentState.MTA })
        {
            string link = Path.Combine(_tempRoot, $"write-{apartment}.lnk");
            RunOnApartment(apartment, () =>
            {
                ShortcutNativeWriteCallResult write = Native.WriteShortcut(
                    link,
                    new ShortcutInfo(
                        target,
                        apartment.ToString(),
                        $"--{apartment}",
                        _tempRoot,
                        string.Empty,
                        0));
                Assert.True(write.Success, write.Detail);
                Assert.Equal(WritePhaseMask, write.AttemptedPhases);
            });
        }
    }

    [Fact]
    public async Task Write_ConcurrentDistinctShortcutsRemainIndependent()
    {
        string target = CreateTarget("write-concurrent.exe");
        Task[] writes = Enumerable.Range(0, 24).Select(index => Task.Run(() =>
        {
            string link = Path.Combine(_tempRoot, $"write-concurrent-{index}.lnk");
            string arguments = $"--index={index}";
            ShortcutNativeWriteCallResult write = Native.WriteShortcut(
                link,
                new ShortcutInfo(
                    target,
                    $"description {index}",
                    arguments,
                    _tempRoot,
                    string.Empty,
                    index));
            Assert.True(write.Success, write.Detail);
            ShortcutInfo stored = ShortcutHelper.ReadStoredMetadataWithCSharpUncached(link);
            Assert.Equal(arguments, stored.Arguments);
            Assert.Equal(index, stored.IconIndex);
        })).ToArray();

        await Task.WhenAll(writes);
    }

    [Fact]
    public void ApplicationShortcutWriteInvalidatesStoredMetadataCache()
    {
        string link = Path.Combine(_tempRoot, "application-cache.lnk");
        string firstTarget = CreateTarget("cache-first.exe");
        string secondTarget = CreateTarget("cache-second.exe");

        DragDropPermissionService.CreateOrUpdateShortcut(link, firstTarget, "--first");
        ShortcutInfo? first = ShortcutHelper.ReadStoredMetadata(link);
        DragDropPermissionService.CreateOrUpdateShortcut(link, secondTarget, "--other");
        ShortcutInfo? second = ShortcutHelper.ReadStoredMetadata(link);

        Assert.Equal(firstTarget, first?.TargetPath);
        Assert.Equal("--first", first?.Arguments);
        Assert.Equal(string.Empty, first?.IconLocation);
        Assert.Equal(secondTarget, second?.TargetPath);
        Assert.Equal("--other", second?.Arguments);
    }

    [Fact]
    public void FolderShortcutWriteCreatesParentAndInvalidatesStoredMetadataCache()
    {
        string link = Path.Combine(_tempRoot, "nested", "folder-cache.lnk");
        string firstTarget = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "folder-cache-first")).FullName;
        string secondTarget = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "folder-cache-second")).FullName;

        ShortcutHelper.CreateOrUpdateFolderShortcut(link, firstTarget, "first description");
        ShortcutInfo? first = ShortcutHelper.ReadStoredMetadata(link);
        ShortcutHelper.CreateOrUpdateFolderShortcut(link, secondTarget, "second description");
        ShortcutInfo? second = ShortcutHelper.ReadStoredMetadata(link);

        Assert.True(File.Exists(link));
        Assert.Equal(firstTarget, first?.TargetPath);
        Assert.Equal("first description", first?.Description);
        Assert.Equal(secondTarget, second?.TargetPath);
        Assert.Equal("second description", second?.Description);
        Assert.Equal(secondTarget, second?.WorkingDirectory);
    }

    [Fact]
    public void StoredRaw_MatchesOnStaAndMtaThreads()
    {
        string link = CreatePathShortcut(
            "apartments.lnk",
            CreateTarget("apartments.exe"),
            arguments: "--apartments");

        RunOnApartment(ApartmentState.STA, () => AssertStoredEqual(link));
        RunOnApartment(ApartmentState.MTA, () => AssertStoredEqual(link));
    }

    [Fact]
    public async Task StoredRaw_ConcurrentReadsMatchCSharpOracle()
    {
        string link = CreatePathShortcut(
            "concurrent.lnk",
            CreateTarget("concurrent.exe"),
            description: "concurrent description",
            arguments: "--concurrent");

        Task[] reads = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            ShortcutInfo csharp = ShortcutHelper.ReadStoredMetadataWithCSharpUncached(link);
            ShortcutNativeCallResult rust = Native.ReadStoredRaw(link);
            Assert.True(rust.Success, rust.Detail);
            AssertShortcutEqual(csharp, rust.Metadata!);
        })).ToArray();

        await Task.WhenAll(reads);
    }

    private ShortcutNativeCallResult AssertResolveEqual(string link, ushort timeoutMs)
    {
        ShortcutInfo? csharp = ShortcutHelper.ResolveWithCSharp(link, timeoutMs);
        ShortcutNativeCallResult rust = Native.ResolveNoUi(link, timeoutMs);

        Assert.NotNull(csharp);
        Assert.True(rust.Success, rust.Detail);
        Assert.Equal(ResolvePhaseMask, rust.AttemptedPhases);
        AssertShortcutEqual(csharp, rust.Metadata!);
        return rust;
    }

    private void AssertStoredEqual(string link)
    {
        ShortcutInfo csharp = ShortcutHelper.ReadStoredMetadataWithCSharpUncached(link);
        ShortcutNativeCallResult rust = Native.ReadStoredRaw(link);
        Assert.True(rust.Success, rust.Detail);
        AssertShortcutEqual(csharp, rust.Metadata!);
    }

    private static void AssertStoredLinksEqual(string expectedLink, string actualLink)
    {
        ShortcutInfo expected = ShortcutHelper.ReadStoredMetadataWithCSharpUncached(expectedLink);
        ShortcutInfo actual = ShortcutHelper.ReadStoredMetadataWithCSharpUncached(actualLink);
        AssertShortcutEqual(expected, actual);
    }

    private string CreateTarget(string name)
    {
        string path = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);
        return path;
    }

    private string CreatePathShortcut(
        string name,
        string target,
        string description = "",
        string arguments = "",
        string workingDirectory = "",
        string iconPath = "",
        int iconIndex = 0)
    {
        string linkPath = Path.Combine(_tempRoot, name);
        IShellLinkW link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetDescription(description);
            link.SetArguments(arguments);
            link.SetWorkingDirectory(workingDirectory);
            link.SetIconLocation(iconPath, iconIndex);
            ((IPersistFile)link).Save(linkPath, true);
            return linkPath;
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    private string CreatePidlShortcut(string name, string parsingName)
    {
        int hresult = SHParseDisplayName(parsingName, 0, out nint pidl, 0, out _);
        Marshal.ThrowExceptionForHR(hresult);
        try
        {
            string linkPath = Path.Combine(_tempRoot, name);
            IShellLinkW link = (IShellLinkW)new ShellLink();
            try
            {
                link.SetIDList(pidl);
                link.SetDescription("PIDL shortcut");
                ((IPersistFile)link).Save(linkPath, true);
                return linkPath;
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    private string CreateRelativePathShortcut(
        string name,
        string fullTarget,
        string relativeTarget)
    {
        string linkPath = Path.Combine(_tempRoot, name);
        IShellLinkW link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(fullTarget);
            link.SetRelativePath(relativeTarget, 0);
            ((IPersistFile)link).Save(linkPath, true);
            return linkPath;
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    private static (bool Success, ShortcutInfo? Metadata) TryReadStoredCSharp(string link)
    {
        try
        {
            return (true, ShortcutHelper.ReadStoredMetadataWithCSharpUncached(link));
        }
        catch
        {
            return (false, null);
        }
    }

    private static void AssertShortcutEqual(ShortcutInfo expected, ShortcutInfo actual)
    {
        Assert.Equal(expected.TargetPath, actual.TargetPath);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.Arguments, actual.Arguments);
        Assert.Equal(expected.WorkingDirectory, actual.WorkingDirectory);
        Assert.Equal(expected.IconLocation, actual.IconLocation);
        Assert.Equal(expected.IconIndex, actual.IconIndex);
    }

    private static void RunOnApartment(ApartmentState apartment, Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(apartment);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), $"{apartment} test thread timed out.");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"{apartment} test thread failed: {failure}");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHParseDisplayName(
        string name,
        nint bindingContext,
        out nint itemIdList,
        uint attributesIn,
        out uint attributesOut);

    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(StringBuilder file, int count, nint findData, uint flags);
        void GetIDList(out nint itemIdList);
        void SetIDList(nint itemIdList);
        void GetDescription(StringBuilder name, int count);
        void SetDescription(string name);
        void GetWorkingDirectory(StringBuilder directory, int count);
        void SetWorkingDirectory(string directory);
        void GetArguments(StringBuilder arguments, int count);
        void SetArguments(string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation(StringBuilder iconPath, int count, out int iconIndex);
        void SetIconLocation(string iconPath, int iconIndex);
        void SetRelativePath(string relativePath, uint reserved);
        void Resolve(nint owner, uint flags);
        void SetPath(string file);
    }
}
