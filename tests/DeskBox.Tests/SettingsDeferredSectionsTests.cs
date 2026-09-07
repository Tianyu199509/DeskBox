using System.Xml.Linq;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class SettingsDeferredSectionsTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Services = "using:DeskBox.Services";

    [Fact]
    public void SearchCatalogCoversUncreatedPagesAndCannotDriftFromTheirXaml()
    {
        string viewsRoot = Path.Combine(FindRepositoryRoot(), "src/DeskBox/Views");
        var expected = new List<SettingsSearchCatalogEntry>();
        foreach (XElement section in GetSectionElements(viewsRoot))
        {
            string tag = GetSectionTag(section);
            var seenHeaders = new HashSet<string>(StringComparer.Ordinal);
            expected.AddRange(ReadEntries(section, tag, viewsRoot)
                .Where(entry => seenHeaders.Add(entry.HeaderKey)));
        }

        Assert.NotEmpty(expected);
        Assert.Equal(expected, SettingsSearchCatalog.Entries);
        Assert.Contains(SettingsSearchCatalog.Entries, entry =>
            entry.SectionTag == "PerformanceSettings" &&
            entry.HeaderKey == "Settings.Performance.ImmediateHiddenWorkingSetTrim.Title");
        Assert.Contains(SettingsSearchCatalog.Entries, entry =>
            entry.SectionTag == "FileStorageSettings" &&
            entry.HeaderKey == "Settings.ManagedPath.DesktopShortcut.Title");
    }

    [Fact]
    public void OnlyGeneralIsInTheInitialVisualTreeAndEveryOtherSectionHasATypedFactory()
    {
        string viewsRoot = Path.Combine(FindRepositoryRoot(), "src/DeskBox/Views");
        XElement host = GetContentHost(viewsRoot);
        XElement initialSection = Assert.Single(host.Elements().Where(IsSection));
        Assert.Equal("General", GetSectionTag(initialSection));
        XElement[] templates = GetSectionTemplates(host).ToArray();
        Assert.NotEmpty(templates);
        Assert.All(templates, template =>
        {
            XElement section = Assert.Single(template.Elements());
            Assert.Equal(GetSectionTag(section) + "SectionTemplate", (string?)template.Attribute(Xaml + "Key"));
            Assert.Equal("viewModels:SettingsViewModel", (string?)template.Attribute(Xaml + "DataType"));
            Assert.Null(section.Attribute(Xaml + "Load"));
        });
    }

    private static IEnumerable<SettingsSearchCatalogEntry> ReadEntries(
        XElement element, string tag, string viewsRoot)
    {
        string name = element.Name.LocalName;
        if (name is "DataTemplate" or "ControlTemplate" or "ResourceDictionary" ||
            name.EndsWith(".Resources", StringComparison.Ordinal))
        {
            yield break;
        }
        if ((string?)element.Attribute(Services + "Localized.HeaderKey") is { Length: > 0 } header)
        {
            yield return new(tag, header,
                (string?)element.Attribute(Services + "Localized.DescriptionKey"));
        }
        // Section references expand by type name: the section file lives in
        // SettingsSections regardless of the namespace its code-behind
        // declares (MusicSettingsSection uses DeskBox.Controls.WidgetContents
        // per the pluginization roadmap 16.4 decision).
        string sectionFilePath = Path.Combine(viewsRoot, "SettingsSections", name + ".xaml");
        if (File.Exists(sectionFilePath))
        {
            XElement nested = XDocument.Load(sectionFilePath).Root!;
            foreach (var entry in ReadEntries(nested, tag, viewsRoot))
            {
                yield return entry;
            }
        }
        foreach (XElement child in element.Elements())
        {
            foreach (var entry in ReadEntries(child, tag, viewsRoot))
            {
                yield return entry;
            }
        }
    }

    private static XElement GetContentHost(string viewsRoot) =>
        XDocument.Load(Path.Combine(viewsRoot, "SettingsWindow.xaml")).Descendants()
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "ContentHost");

    private static bool IsSection(XElement element) =>
        ((string?)element.Attribute(Xaml + "Name"))?.EndsWith("Section", StringComparison.Ordinal) == true;

    private static IEnumerable<XElement> GetSectionTemplates(XElement host) =>
        host.Descendants().Where(element => element.Name.LocalName == "DataTemplate" &&
            ((string?)element.Attribute(Xaml + "Key"))?.EndsWith("SectionTemplate", StringComparison.Ordinal) == true);

    private static IEnumerable<XElement> GetSectionElements(string viewsRoot)
    {
        XElement host = GetContentHost(viewsRoot);
        return host.Elements().Where(IsSection).Concat(GetSectionTemplates(host).SelectMany(template => template.Elements()));
    }

    private static string GetSectionTag(XElement section) =>
        ((string)section.Attribute(Xaml + "Name")!)[..^"Section".Length];

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DeskBox.sln")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("DeskBox repository root was not found.");
    }
}
