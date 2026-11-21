using System.Windows.Controls;

namespace GestureFlow.Desktop;

public partial class MainWindow
{
    private OfflineArticle? _helpArticle;
    private OfflineArticle? _aboutArticle;

    private void NoticeClick(object sender, System.Windows.RoutedEventArgs e) => ResolveOfflineLink("SAFETY.md")?.Invoke();

    private void HelpTopicChanged(object sender, SelectionChangedEventArgs e) => UpdateInformationPage();

    private void AboutTopicChanged(object sender, SelectionChangedEventArgs e) => UpdateInformationPage();

    private void UpdateInformationPage()
    {
        switch (_shell.CurrentPage?.Page)
        {
            case DesktopPage.Help when HelpReader is not null && HelpTopicPicker.SelectedItem is OfflineArticle help:
                ShowArticle(HelpReader, help, ref _helpArticle);
                break;
            case DesktopPage.About when AboutReader is not null && AboutTopicPicker.SelectedItem is OfflineArticle about:
                ShowArticle(AboutReader, about, ref _aboutArticle);
                break;
        }
    }

    private void ShowArticle(FlowDocumentScrollViewer reader, OfflineArticle article, ref OfflineArticle? current)
    {
        if (current == article)
            return;

        try
        {
            reader.Document = OfflineDocumentRenderer.Render(DesktopInformation.Read(article), article.IsMarkdown, ResolveOfflineLink);
            current = article;
        }
        catch (Exception error) when (IsReadError(error))
        {
            current = null;
            reader.Document = OfflineDocumentRenderer.Render($"无法读取离线文档 · {error.Message}", false, ResolveOfflineLink);
        }
    }

    private Action? ResolveOfflineLink(string resourceName)
    {
        var information = _shell.Information;
        var help = information.HelpTopics.FirstOrDefault(article =>
            string.Equals(article.ResourceName, resourceName, StringComparison.OrdinalIgnoreCase));
        if (help is not null)
            return () => OpenArticle(DesktopPage.Help, HelpTopicPicker, help);

        var about = information.AboutTopics.FirstOrDefault(article =>
            string.Equals(article.ResourceName, resourceName, StringComparison.OrdinalIgnoreCase));
        return about is null ? null : () => OpenArticle(DesktopPage.About, AboutTopicPicker, about);
    }

    private void OpenArticle(DesktopPage page, ComboBox picker, OfflineArticle article)
    {
        picker.SetCurrentValue(ComboBox.SelectedItemProperty, article);
        _shell.NavigateTo(page);
    }
}
