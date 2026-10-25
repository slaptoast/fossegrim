namespace Fossegrim.Web.Services;

/// <summary>
/// Short-lived confirmation messages ("Added to queue"), shown by the Toasts
/// component in MainLayout and dismissed automatically.
/// </summary>
public class ToastService
{
    public event Action<string>? Shown;

    public void Show(string message) => Shown?.Invoke(message);
}
