#if WINDOWS
using System.Drawing;

namespace CloudClipboard.Agent.Services;

public interface IAppIconProvider : IDisposable
{
    Icon GetIcon(int size);
}
#else
namespace CloudClipboard.Agent.Services;

public interface IAppIconProvider
{
}
#endif
