using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using ScrollViewer = System.Windows.Controls.ScrollViewer;

namespace GotifyDesktop;

public partial class MainWindow
{
    private bool _readerSuspended, _batchingHistory;
    private bool _richReaderReleased;
    private double _savedListOffset, _savedDetailOffset, _savedRichOffset;
    private DispatcherTimer? _memoryIdleTimer;
    internal bool ReaderSuspended => _readerSuspended;
    private void InitializeMemoryLifecycle()
    {
        _memoryIdleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _memoryIdleTimer.Tick += (_, _) =>
        {
            _memoryIdleTimer.Stop();
            // A single opportunistic collection after releasing the hidden view. Do not trim
            // the process working set or run periodic forced collections.
            if (_readerSuspended && _richReaderReleased && !_exiting) GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
        };
        IsVisibleChanged += (_, _) =>
        {
            if (_exiting) return;
            if (IsVisible) RestoreReader(); else SuspendReader();
        };
    }
    private void SuspendReader()
    {
        if (_readerSuspended) return;
        _savedListOffset = FindMemoryVisual<ScrollViewer>(MessagesList)?.VerticalOffset ?? 0;
        _savedDetailOffset = DetailScroll.VerticalOffset;
        _savedRichOffset = DetailRichBody.VerticalOffset;
        _readerSuspended = true;
        // The virtualized list keeps only a few containers. Reuse them: tearing down and
        // rebuilding these controls increases native WPF allocations on every reopen.
        if (_selectedMessage?.Message.Length > 8000)
        {
            DetailPanel.DataContext = null; ReleaseRichReader(); _richReaderReleased = true;
        }
        _filterTimer?.Stop();
        if (_richReaderReleased) _memoryIdleTimer?.Start();
    }
    private void RestoreReader()
    {
        _memoryIdleTimer?.Stop();
        if (!_readerSuspended) return;
        _readerSuspended = false;
        _view.Refresh();
        var selected = _selectedMessage;
        if (selected != null && _messages.Contains(selected))
        {
            if (_richReaderReleased) SelectMessage(selected);
        }
        else ClearDetails();
        _richReaderReleased = false;
        _filterTimer?.Start(); UpdateCount();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_readerSuspended || _exiting) return;
            FindMemoryVisual<ScrollViewer>(MessagesList)?.ScrollToVerticalOffset(_savedListOffset);
            DetailScroll.ScrollToVerticalOffset(_savedDetailOffset);
            DetailRichBody.ScrollToVerticalOffset(_savedRichOffset);
        }));
    }
    private void ReleaseRichReader()
    {
        DetailRichBody.Document = new FlowDocument();
        _renderedMessage = null; _renderedBody = null; _renderedExtras = null;
        VerificationCodeBox.Clear(); _verificationCode = ""; _originalUrl = null;
        OpenOriginalButton.ToolTip = CopyCodeButton.ToolTip = null;
    }
    private static T? FindMemoryVisual<T>(DependencyObject node) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T match) return match;
            if (FindMemoryVisual<T>(child) is T nested) return nested;
        }
        return null;
    }
}
