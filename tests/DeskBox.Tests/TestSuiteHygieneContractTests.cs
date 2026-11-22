using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace DeskBox.Tests;

public sealed class TestSuiteHygieneContractTests
{
    private static readonly Regex HistoricalGrabBagFileNamePattern = new(
        @"^(AotStage|Phase\d|P\dBatch|Round\d|Acceptance\d|DragAuditRemediation)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SilentComExceptionSkipPattern = new(
        @"catch\s*\(\s*(?:System\.Runtime\.InteropServices\.)?COMException(?:\s+\w+)?\s*\)\s*\{\s*return\s*;\s*\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] BehavioralOnlyTestFiles =
    [
        "AppearanceSettingsEditorTests.cs",
        "FileSettingsEditorTests.cs",
        "FileStackSettingsEditorTests.cs",
        "GroupAndCapsuleSettingsEditorTests.cs",
        "InteractionSettingsEditorTests.cs",
        "MusicSettingsEditorPilotTests.cs",
        "PerformanceSettingsEditorTests.cs",
        "QuickCaptureSettingsEditorTests.cs",
        "TodoSettingsEditorTests.cs",
        "WeatherSettingsEditorTests.cs",
        "CompanionResolutionAndStudioRetryTests.cs",
        "FileDragTransferRemediationTests.cs",
        "ManagedStorageAndDropStagingSafetyTests.cs",
        "StorageRebaseAndAttachmentLifecycleTests.cs",
        "StorageMigrationAndFolderWatcherBurstTests.cs",
        "FileTransferSafetyAndRollbackTests.cs",
        "CustomWidgetAgentAndLocalizationTests.cs",
        "DropIntentAndOriginClassificationTests.cs",
        "CustomWidgetRuntimeHardeningTests.cs",
        "CustomWidgetStudioAiAndMcpTests.cs",
        "FileStackPopoverAndGroupDropRoutingTests.cs",
        "CustomWidgetCatalogSyncAndSandboxTests.cs",
        "CustomWidgetRemoteCatalogAndOtaTests.cs",
        "OutboundShellDragEngineTests.cs",
        "ExecutableDropAndFolderMergeSafetyTests.cs",
        "FileSurfaceAndSearchDragInteractionTests.cs",
        "VirtualDropAndShellMountCompatibilityTests.cs",
        "CallbackDeferralContractTests.cs",
        "DiagnosticsBundleRegressionTests.cs",
        "TodoQuickCaptureUxUpgradeTests.cs",
        "FileTransferUndoIdentityPropertyTests.cs",
        "FileOpenEcosystemHardeningTests.cs",
        "CustomWidgetStudioArchitectureTests.cs",
        "AiTransportUnificationTests.cs",
        "CustomWidgetCapabilityHostTests.cs",
        "CustomWidgetOfficialTemplateCleanlinessTests.cs",
        "CrossPlatformAndLocaleCompatibilityTests.cs",
        "CustomWidgetCompatibilityPromptAndLocaleTests.cs",
    ];

    [Fact]
    public void TestFiles_AreNamedByDomain_NotByStagePhaseBatchOrTicketHistory()
    {
        var testsDirectory = TestPaths.FromRepository("tests/DeskBox.Tests");
        var offendingFiles = Directory
            .EnumerateFiles(testsDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name) && HistoricalGrabBagFileNamePattern.IsMatch(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offendingFiles.Length == 0,
            $"Test files must be named after the domain under test rather than historical stage/phase/batch/ticket labels. Offending files: {string.Join(", ", offendingFiles)}");
    }

    [Fact]
    public void TestSuite_NeverSilentlySkipsTestsViaDispatcherAvailabilityFlagsOrComExceptionEarlyReturns()
    {
        var testsDirectory = TestPaths.FromRepository("tests/DeskBox.Tests");
        foreach (var filePath in Directory.EnumerateFiles(testsDirectory, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(filePath);
            if (string.Equals(fileName, nameof(TestSuiteHygieneContractTests) + ".cs", StringComparison.Ordinal))
            {
                continue;
            }

            var source = File.ReadAllText(filePath);
            Assert.DoesNotContain("s_realDispatcherQueueAvailable", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ProbeRealDispatcherQueue", source, StringComparison.Ordinal);
            Assert.False(
                SilentComExceptionSkipPattern.IsMatch(source),
                $"{fileName} contains a silent `catch (COMException) {{ return; }}` fake-green skip. Use real WinRT dispatcher activation via TestHostWinRtActivationInitializer instead.");
        }
    }

    private static readonly Regex ImplementationSourceReadPattern = new(
        @"(?:File\.ReadAll(?:Text|Lines)|TestPaths\.FromRepository|Read[A-Za-z0-9_]*File)\s*\(\s*""src/DeskBox/[^""]*\.(?:cs|xaml)""|""src/DeskBox/[^""]*\.(?:cs|xaml)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void BehavioralDomainAndSettingsEditorTests_DoNotPinImplementationSourceViaFileReadAllText()
    {
        var testsDirectory = TestPaths.FromRepository("tests/DeskBox.Tests");
        foreach (var fileName in BehavioralOnlyTestFiles)
        {
            var filePath = Path.Combine(testsDirectory, fileName);
            Assert.True(File.Exists(filePath), $"Expected behavioral test file to exist: {fileName}");

            var source = File.ReadAllText(filePath);
            Assert.False(
                ImplementationSourceReadPattern.IsMatch(source),
                $"{fileName} pins src/DeskBox implementation .cs/.xaml source text instead of testing runtime behavior or reflection contracts.");
        }
    }
}
