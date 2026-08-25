using System.Windows.Threading;

namespace OpenCvSharp
{
    /// <summary>
    /// Keeps the analysis code readable while targeting the OpenCvSharp enum name used by the Windows runtime package.
    /// </summary>
    internal static class CmpType
    {
        internal const CmpTypes LT = CmpTypes.LT;
    }
}

namespace System.Windows.Threading
{
    internal static class TranscencodeDispatcherExtensions
    {
        internal static DispatcherOperation BeginInvoke(this Dispatcher dispatcher, Action action)
        {
            ArgumentNullException.ThrowIfNull(dispatcher);
            ArgumentNullException.ThrowIfNull(action);
            return dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
        }
    }
}
