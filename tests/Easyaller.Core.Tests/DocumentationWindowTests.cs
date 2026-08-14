using Easyaller.App;

namespace Easyaller.Core.Tests;

public sealed class DocumentationWindowTests
{
    [Fact]
    public void EmbeddedRussianGuide_IsAvailableOffline()
    {
        var text = DocumentationWindow.LoadDocumentation();

        Assert.Contains("Начало работы с Easyaller", text);
        Assert.Contains("Выборочное применение", text);
        Assert.Contains("Архив Outlook", text);
    }
}
