using System.Drawing;

namespace CloudClipboard.Agent.Windows.Services;

public interface IAppIconProvider : IDisposable
{
    Icon GetIcon(int size);
}
