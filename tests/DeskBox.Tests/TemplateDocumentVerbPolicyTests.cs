using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// The template extension table (feedback 366 / confirmed 81): every listed
/// extension names a document class whose Shell default verb creates a copy
/// ("New"), so an explicit "open" verb must never be dispatched for it.
/// Extensions that merely live near the family — add-ins, themes, regular
/// documents, shortcuts — must stay out of the table.
/// </summary>
public sealed class TemplateDocumentVerbPolicyTests
{
    [Theory]
    [InlineData(".xltx")]
    [InlineData(".xltm")]
    [InlineData(".dotx")]
    [InlineData(".dotm")]
    [InlineData(".potx")]
    [InlineData(".potm")]
    [InlineData(".vstx")]
    [InlineData(".vstm")]
    [InlineData(".xlt")]
    [InlineData(".dot")]
    [InlineData(".ots")]
    [InlineData(".ott")]
    public void IsTemplateDocument_RecognizesEveryTemplateExtension(string extension)
    {
        Assert.True(TemplateDocumentVerbPolicy.IsTemplateDocument(
            @"C:\Templates\Report" + extension));
    }

    [Theory]
    [InlineData(".XLTX")]
    [InlineData(".DotX")]
    [InlineData(".OTT")]
    public void IsTemplateDocument_IgnoresExtensionCase(string extension)
    {
        Assert.True(TemplateDocumentVerbPolicy.IsTemplateDocument(
            @"C:\Templates\Report" + extension));
    }

    [Theory]
    [InlineData(".xlsx")]   // regular workbook: "open" is the desktop verb
    [InlineData(".xlsm")]
    [InlineData(".docx")]
    [InlineData(".pptx")]
    [InlineData(".xlam")]   // Excel add-in: loaded, not copied
    [InlineData(".thmx")]   // Office theme: open semantics
    [InlineData(".vst")]    // legacy Visio template that doubles as Targa
    [InlineData(".exe")]
    [InlineData(".lnk")]
    [InlineData(".txt")]
    [InlineData(".xltx2")]  // look-alike extension, not the real thing
    public void IsTemplateDocument_RejectsNonTemplateExtensions(string extension)
    {
        Assert.False(TemplateDocumentVerbPolicy.IsTemplateDocument(
            @"C:\Templates\Report" + extension));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\Templates\README")]
    [InlineData(@"C:\Templates\")]
    public void IsTemplateDocument_RejectsUnusableOrExtensionlessPaths(string? path)
    {
        Assert.False(TemplateDocumentVerbPolicy.IsTemplateDocument(path));
    }
}
