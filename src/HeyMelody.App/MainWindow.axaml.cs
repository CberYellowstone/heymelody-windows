using Avalonia.Controls;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;

namespace HeyMelodyWindows.App;

public partial class MainWindow : FAAppWindow
{
    private readonly MainViewModel _viewModel;
    private readonly WindowConfirmationService _confirmations;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(8) };
    private bool _closing, _closed;

    public MainWindow() : this(HeadsetServiceFactory.Create(), false) { }

    internal MainWindow(IHeadsetService service, bool preview)
    {
        InitializeComponent();
        _confirmations = new WindowConfirmationService(this);
        _viewModel = new MainViewModel(service, _confirmations, preview);
        Page.DataContext = _viewModel;
        Page.SetPlatformNotice(!OperatingSystem.IsWindows());
        _poll.Tick += async (_, _) => await _viewModel.PollAsync();
        Opened += async (_, _) => { await _viewModel.InitializeAsync(); if (!_closing) _poll.Start(); };
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _viewModel.SuspendActionsForClose();
        _confirmations.CancelActiveDialog();
        await Page.DismissSettingsDialogAsync();
        if (!await _viewModel.RequestCloseAsync()) { _closing = false; Page.ResumeSettingsDialogs(); return; }
        _poll.Stop();
        await _viewModel.DisposeAsync();
        _closed = true;
        Dispatcher.UIThread.Post(Close);
    }
}

internal sealed class WindowConfirmationService(TopLevel owner) : IConfirmationService
{
    private FAContentDialog? _activeDialog;
    public void CancelActiveDialog() => _activeDialog?.Hide();
    public Task<bool> ConfirmAsync(string title, string message, CancellationToken token) =>
        ShowConfirmationAsync(title, message, "继续", token);

    public Task<bool> ConfirmRetryAsync(string title, string message, CancellationToken token) =>
        ShowConfirmationAsync(title, message, "确认", token);

    public async Task AlertAsync(string title, string message, CancellationToken token) =>
        _ = await ShowDialogAsync(CreateAlert(title, message), token);

    private async Task<bool> ShowConfirmationAsync(string title, string message, string primaryText, CancellationToken token) =>
        await ShowDialogAsync(CreateConfirmation(title, message, primaryText), token) == FAContentDialogResult.Primary
            && !token.IsCancellationRequested;

    internal static FAContentDialog CreateConfirmation(string title, string message, string primaryText) =>
        new()
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = FAContentDialogButton.Close
        };

    internal static FAContentDialog CreateAlert(string title, string message) =>
        new()
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            CloseButtonText = "确定",
            DefaultButton = FAContentDialogButton.Close
        };

    private async Task<FAContentDialogResult> ShowDialogAsync(FAContentDialog dialog, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _activeDialog = dialog;
        using var registration = token.Register(() => Dispatcher.UIThread.Post(dialog.Hide));
        try { return await dialog.ShowAsync(owner); }
        finally { _activeDialog = null; }
    }
}
