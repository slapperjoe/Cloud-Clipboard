namespace CloudClipboard.Agent.UI;

/// <summary>
/// Abstraction over platform-specific system tray icon.
/// </summary>
public interface ITrayIcon
{
    /// <summary>
    /// Shows the tray icon.
    /// </summary>
    void Show();

    /// <summary>
    /// Hides the tray icon.
    /// </summary>
    void Hide();

    /// <summary>
    /// Sets the tray icon tooltip/description.
    /// </summary>
    void SetTooltip(string tooltip);

    /// <summary>
    /// Adds or updates a menu item.
    /// </summary>
    void SetMenu(string menuXml);

    /// <summary>
    /// Raised when a menu item is activated.
    /// </summary>
    event Action<string> ItemActivated;
}
