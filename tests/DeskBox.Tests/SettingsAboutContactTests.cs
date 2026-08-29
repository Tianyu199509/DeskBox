namespace DeskBox.Tests;

public sealed class SettingsAboutContactTests
{
    [Fact]
    public void AboutSection_ShowsFeedbackEmailAndNoRepositoryButton()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.xaml"));
        string viewModel = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/ViewModels/SettingsViewModel.cs"));
        string aboutViewModel = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/ViewModels/SettingsViewModel.AboutAndUpdates.cs"));
        string responsiveLayout = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.xaml.cs"));
        string dialogCode = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.DataTools.cs"));
        string storeActions = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.StorageAndUpdates.cs"));
        string project = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/DeskBox.csproj"));
        string zhCn = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Strings/zh-CN.json"));
        var xamlDocument = System.Xml.Linq.XDocument.Parse(xaml);
        var versionText = Assert.Single(xamlDocument
            .Descendants()
            .Where(element =>
                element.Name.LocalName == "TextBlock" &&
                string.Equals(
                    element.Attribute("Text")?.Value,
                    "{Binding AppVersion}",
                    StringComparison.Ordinal)));
        var supportDialogContent = Assert.Single(xamlDocument
            .Descendants()
            .Where(element =>
                element.Name.LocalName == "StackPanel" &&
                string.Equals(
                    element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "Name")?.Value,
                    "SupportDialogContent",
                    StringComparison.Ordinal)));
        var supportQrContainers = xamlDocument
            .Descendants()
            .Where(element =>
                element.Name.LocalName == "Border" &&
                element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "Name" &&
                    (string.Equals(attribute.Value, "WechatSupportQrContainer", StringComparison.Ordinal) ||
                     string.Equals(attribute.Value, "AlipaySupportQrContainer", StringComparison.Ordinal))))
            .ToArray();
        var supportStoreButton = Assert.Single(xamlDocument
            .Descendants()
            .Where(element =>
                element.Name.LocalName == "Button" &&
                string.Equals(
                    element.Attribute("Click")?.Value,
                    "OpenMicrosoftStoreButton_Click",
                    StringComparison.Ordinal)));

        Assert.Contains("Settings.About.FeedbackTitle", xaml, StringComparison.Ordinal);
        Assert.Contains("FeedbackEmailButton", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AboutRightPanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Spacing=\"6\"", xaml, StringComparison.Ordinal);
        Assert.Contains("1047078635@qq.com", viewModel, StringComparison.Ordinal);
        Assert.Contains("FeedbackEmailButton.HorizontalAlignment", responsiveLayout, StringComparison.Ordinal);
        Assert.Contains("Grid.SetRow(AboutRightPanel", responsiveLayout, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AboutMeDialog\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowAboutMeButton_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("Settings.Dialog.AboutMeP1", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding AppVersion}\"", xaml, StringComparison.Ordinal);
        Assert.Null(versionText.Attribute("Foreground"));
        Assert.Contains("ms-appx:///Assets/wechat-qrcode.jpg", xaml, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(xaml, "ms-appx:///Assets/wechat-qrcode.jpg"));
        Assert.Contains("StoreSupportCardVisibility", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SupportDeskBoxDialog\"", xaml, StringComparison.Ordinal);
        Assert.Equal("480", supportDialogContent.Attribute("Width")?.Value);
        Assert.Equal("480", supportDialogContent.Attribute("MaxWidth")?.Value);
        Assert.Equal(2, supportQrContainers.Length);
        Assert.All(supportQrContainers, container =>
        {
            Assert.Equal("108", container.Attribute("Width")?.Value);
            Assert.Equal("108", container.Attribute("Height")?.Value);
        });
        Assert.Equal("12,5,12,7", supportStoreButton.Attribute("Padding")?.Value);
        Assert.Equal("Right", supportStoreButton.Attribute("HorizontalAlignment")?.Value);
        Assert.Equal("Center", supportStoreButton.Attribute("VerticalAlignment")?.Value);
        Assert.Contains("ShowStoreSupportDialogButton_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("OpenMicrosoftStoreButton_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("ms-appx:///Assets/Support/support-wechat.png", xaml, StringComparison.Ordinal);
        Assert.Contains("ms-appx:///Assets/Support/support-alipay.png", xaml, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(xaml, "ms-appx:///Assets/Support/support-wechat.png"));
        Assert.Equal(1, CountOccurrences(xaml, "ms-appx:///Assets/Support/support-alipay.png"));
        Assert.Contains("https://apps.microsoft.com/detail/", viewModel, StringComparison.Ordinal);
        Assert.Contains("9PBZSNB4D69H", viewModel, StringComparison.Ordinal);
        Assert.Contains("deskbox_about_support", viewModel, StringComparison.Ordinal);
        Assert.Contains("?cid=", viewModel, StringComparison.Ordinal);
        Assert.Contains("ms-windows-store://pdp/?ProductId=", viewModel, StringComparison.Ordinal);
        Assert.Contains("&cid=", viewModel, StringComparison.Ordinal);
        Assert.Contains("IsDirectInstallerUpdateDelivery ? Visibility.Visible : Visibility.Collapsed", aboutViewModel, StringComparison.Ordinal);
        Assert.Contains("AboutMeDialog.ShowAsync", dialogCode, StringComparison.Ordinal);
        Assert.Contains("SupportDeskBoxDialog.ShowAsync", storeActions, StringComparison.Ordinal);
        Assert.Contains("ViewModel.StoreSupportCardVisibility != Visibility.Visible", storeActions, StringComparison.Ordinal);
        Assert.Contains("Launcher.LaunchUriAsync(new Uri(ViewModel.MicrosoftStoreAppLink))", storeActions, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.OpenFile(ViewModel.MicrosoftStoreLink)", storeActions, StringComparison.Ordinal);
        Assert.Contains("感谢每一位愿意使用和支持 DeskBox 的朋友。", zhCn, StringComparison.Ordinal);
        Assert.Contains("无论是否支持，DeskBox 都会继续认真维护，努力为大家带来更好的使用体验。", zhCn, StringComparison.Ordinal);
        Assert.DoesNotContain("不会解锁额外功能", zhCn, StringComparison.Ordinal);
        Assert.DoesNotContain("暂时不支持", zhCn, StringComparison.Ordinal);
        Assert.Contains("Assets\\Support\\*.png", project, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, "src", "DeskBox", "Assets", "Support", "support-wechat.png")));
        Assert.True(File.Exists(Path.Combine(root, "src", "DeskBox", "Assets", "Support", "support-alipay.png")));
        Assert.DoesNotContain("AboutRepositoryButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AboutRepositoryButton", responsiveLayout, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenRepositoryButton_Click", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowProductReasonButton_Click", dialogCode, StringComparison.Ordinal);
        Assert.DoesNotContain("AboutMeDialog.Title", dialogCode, StringComparison.Ordinal);
        Assert.DoesNotContain("AboutVersionTextBlock", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AboutDeveloperText", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.Dialog.AboutMeP3", xaml, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int offset = 0;

        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "src",
                    "DeskBox",
                    "DeskBox.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "DeskBox repository root was not found.");
    }
}
