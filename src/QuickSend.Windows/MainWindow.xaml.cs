using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Eslee.QuickSend.Core.Transfers;
using Eslee.QuickSend.Windows.Persistence;
using Eslee.QuickSend.Windows.Transfers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Eslee.QuickSend.Windows;

public sealed partial class MainWindow : Window
{
    private const int SwRestore = 9;
    private readonly ObservableCollection<DeviceListItem> _devices = [];
    private readonly ObservableCollection<HistoryListItem> _history = [];
    private readonly DispatcherQueue _uiDispatcher;
    private TrayIconService? _tray;
    private bool _allowClose;
    private bool _closed;
    private int _historyRefreshQueued;

    public MainWindow(DispatcherQueue uiDispatcher)
    {
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        if (!_uiDispatcher.HasThreadAccess)
            throw new InvalidOperationException("MainWindow must be constructed on the captured WinUI thread.");

        AppServices.Log.Info("window.constructor.start", new { threadId = Environment.CurrentManagedThreadId });
        InitializeComponent();
        AppServices.Log.Info("window.xaml.initialized");

        DeviceList.ItemsSource = _devices;
        HistoryList.ItemsSource = _history;
        DeviceNameText.Text = AppServices.DeviceName.Current;
        ReceiveFolderText.Text = $"받은 파일: {AppServices.ReceiveDirectory}";
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(1080, 760));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.PreferredMinimumWidth = 840;

        Closed += Window_Closed;
        AppWindow.Closing += AppWindow_Closing;
        AppServices.Discovery.DeviceChanged += Discovery_DeviceChanged;
        AppServices.Coordinator.ProgressChanged += Coordinator_ProgressChanged;
        AppServices.Coordinator.PairingRequested += Coordinator_PairingRequested;
        AppServices.Coordinator.Peers.Changed += Coordinator_PeerConnectionChanged;
        AppServices.DeviceName.NameChanged += DeviceName_Changed;
        // History refreshes on committed repository writes and on queue changes, so both
        // send and receive appear without the user pressing 새로 고침.
        AppServices.TransferStore.Changed += Store_Changed;
        AppServices.History.Changed += Store_Changed;
        AppServices.Coordinator.RunningTransfersChanged += Store_Changed;

        try
        {
            _tray = new TrayIconService(AppServices.Log, WindowNative.GetWindowHandle(this), _uiDispatcher);
            _tray.OpenRequested += Tray_OpenRequested;
            _tray.PauseRequested += Tray_PauseRequested;
            _tray.ExitRequested += Tray_ExitRequested;
            AppServices.Log.Info("window.tray.attached");
        }
        catch (Exception ex)
        {
            AppServices.Log.Error("window.tray.initialization.failed", ex);
            _tray?.Dispose();
            _tray = null;
        }

        LogWindowState("constructor.complete");
    }

    internal async Task OnServicesReadyAsync()
    {
        await EnqueueUiAsync("services-ready", async () =>
        {
            await RefreshDevicesAsync();
            await RefreshHistoryAsync();
            UpdateIdleStatus();
            AppServices.Log.Info("window.services.ready");
        });
    }

    internal void ShowInitializationError(Exception exception) => EnqueueUi("initialization-error", () =>
    {
        StatusText.Text = $"초기화하지 못했습니다: {exception.Message}";
        ShowAndActivateCore("initialization-error");
    });

    internal void ShowAndActivate(string source)
    {
        if (_uiDispatcher.HasThreadAccess)
            ShowAndActivateCore(source);
        else
            EnqueueUi($"show:{source}", () => ShowAndActivateCore(source));
    }

    internal void AllowClose() => _allowClose = true;

    private void ShowAndActivateCore(string source)
    {
        if (_closed)
        {
            AppServices.Log.Warn("window.activation.skipped.closed", new { source });
            return;
        }

        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter &&
                presenter.State == OverlappedPresenterState.Minimized)
                presenter.Restore();
            AppWindow.Show();
            Activate();
            var hwnd = WindowNative.GetWindowHandle(this);
            ShowWindow(hwnd, SwRestore);
            var foreground = SetForegroundWindow(hwnd);
            AppServices.Log.Info("window.activation.succeeded", new
            {
                source,
                hwnd = $"0x{hwnd.ToInt64():X}",
                foreground,
                visible = IsWindowVisible(hwnd),
                presenterState = (AppWindow.Presenter as OverlappedPresenter)?.State.ToString()
            });
        }
        catch (Exception ex)
        {
            AppServices.Log.Error("window.activation.failed", ex);
            throw;
        }
    }

    private void Discovery_DeviceChanged(object? sender, EventArgs e) =>
        EnqueueUiAsyncFireAndForget("discovery-device-changed", RefreshDevicesAsync);

    private void Coordinator_ProgressChanged(object? sender, TransferUiState state) => EnqueueUi("transfer-progress", () =>
    {
        StatusText.Text = state.StatusText;
        TransferMetricsPanel.Visibility = state.HasTransfer ? Visibility.Visible : Visibility.Collapsed;
        CurrentFileText.Text = state.CurrentFile;
        JobProgress.Value = state.Percent;
        TransferredText.Text = $"{FormatBytes(state.SafeBytes)} / {FormatBytes(state.TotalBytes)}";
        SpeedText.Text = $"{FormatBytes((long)state.BytesPerSecond)}/s";
        EtaText.Text = state.EstimatedRemaining is { } eta ? $"약 {eta:g} 남음" : "남은 시간 계산 중";
        PauseButton.IsEnabled = state.CanPause;
        CancelButton.IsEnabled = state.CanCancel;
    });

    private void Store_Changed(object? sender, EventArgs e) => ScheduleHistoryRefresh();

    /// <summary>
    /// Coalesces a burst of repository writes into a single UI refresh on the dispatcher
    /// thread. A queued refresh always re-queries after the writes that triggered it, so a
    /// row can never be missed by a refresh that ran too early.
    /// </summary>
    private void ScheduleHistoryRefresh()
    {
        if (_closed) return;
        if (Interlocked.Exchange(ref _historyRefreshQueued, 1) != 0) return;
        var queued = _uiDispatcher.TryEnqueue(async () =>
        {
            Interlocked.Exchange(ref _historyRefreshQueued, 0);
            try { await RefreshHistoryAsync(); }
            catch (Exception ex) { AppServices.Log.Error("history.auto_refresh.failed", ex); }
        });
        if (!queued)
        {
            Interlocked.Exchange(ref _historyRefreshQueued, 0);
            AppServices.Log.Warn("history.auto_refresh.enqueue.failed", new { });
        }
    }

    private void Coordinator_PairingRequested(object? sender, PairingPrompt prompt) =>
        EnqueueUiAsyncFireAndForget("pairing-request", async () =>
        {
            var accepted = false;
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    Title = $"{prompt.DeviceName}와 연결하시겠습니까?",
                    Content = $"두 기기에 같은 인증번호가 표시되는지 확인하세요.\n\n{prompt.Code}",
                    PrimaryButtonText = "신뢰",
                    CloseButtonText = "거부",
                    DefaultButton = ContentDialogButton.Primary
                };
                accepted = await dialog.ShowAsync() == ContentDialogResult.Primary;
            }
            finally
            {
                AppServices.Coordinator.CompletePairing(prompt.RequestId, accepted);
            }
        });

    private async void Select_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceListItem selected)
        {
            await ShowMessageAsync("기기를 선택하세요", "먼저 주변 기기에서 받을 기기를 선택해 주세요.");
            return;
        }

        var picker = new global::Windows.Storage.Pickers.FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;
        var result = await AppServices.Coordinator.QueueFilesAsync(selected.DeviceId, files.Select(static f => f.Path).ToArray());
        await ShowSkippedItemsIfNeededAsync(result);
    }

    private async void SelectFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceListItem selected)
        {
            await ShowMessageAsync("기기를 선택하세요", "먼저 주변 기기에서 받을 기기를 선택해 주세요.");
            return;
        }
        var picker = new global::Windows.Storage.Pickers.FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            var result = await AppServices.Coordinator.QueueFilesAsync(selected.DeviceId, [folder.Path]);
            await ShowSkippedItemsIfNeededAsync(result);
        }
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => AppServices.Coordinator.TogglePause();

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "전송을 취소하시겠습니까?",
            Content = $"현재 {TransferredText.Text}를 안전하게 받았습니다. 취소하면 미완료 데이터가 삭제됩니다.",
            PrimaryButtonText = "취소하고 삭제",
            CloseButtonText = "계속 전송",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await AppServices.Coordinator.CancelAsync(deletePartial: true);
    }

    private async void Discover_Click(object sender, RoutedEventArgs e) => await AppServices.Discovery.RefreshAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshHistoryAsync();

    private async void DeleteHistory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid fileId }) return;

        var deleted = await AppServices.History.DeleteFileAsync(fileId, AppServices.Coordinator.RunningTransferIds);
        AppServices.Log.Info("history.file.delete", new { fileId, deleted });
        if (!deleted)
        {
            await ShowMessageAsync("기록을 삭제할 수 없습니다", "진행 중인 전송 기록은 전송이 끝나거나 취소된 뒤 삭제할 수 있습니다.");
            return;
        }
        await RefreshHistoryAsync();
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "완료된 전송 기록을 정리하시겠습니까?",
            Content = "완료·취소·실패 기록과, 더 이상 진행되지 않는 오래된 기록을 목록에서 삭제합니다. 진행 중인 전송, 보낸 원본 파일, 받은 파일은 그대로 유지됩니다.",
            PrimaryButtonText = "기록 정리",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var result = await AppServices.History.ClearAsync(AppServices.Coordinator.RunningTransferIds);
        AppServices.Log.Info("history.clear", new { result.DeletedFiles, result.DeletedJobs, result.KeptRunningJobs });
        await RefreshHistoryAsync();
        if (result.KeptRunningJobs > 0)
            await ShowMessageAsync("일부 기록을 유지했습니다", $"진행 중인 전송 {result.KeptRunningJobs}건의 기록은 삭제하지 않았습니다.");
    }

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePeerConnectionControls();
        if (!AppServices.Coordinator.HasActiveTransfer) UpdateIdleStatus();
    }

    private void Coordinator_PeerConnectionChanged(object? sender, string deviceId) =>
        EnqueueUi("peer-connection-changed", UpdatePeerConnectionControls);

    private void DeviceName_Changed(object? sender, string name) =>
        EnqueueUi("device-name-changed", () => DeviceNameText.Text = name);

    /// <summary>
    /// Keeps the transfer buttons and the disconnect/reconnect toggle in sync with the
    /// selected peer. A manual disconnect blocks transfers until the user reconnects.
    /// </summary>
    private void UpdatePeerConnectionControls()
    {
        if (DeviceList.SelectedItem is not DeviceListItem selected)
        {
            SelectButton.IsEnabled = false;
            SelectFolderButton.IsEnabled = false;
            PeerConnectionButton.IsEnabled = false;
            PeerConnectionButton.Content = "연결 끊기";
            PeerConnectionHint.Text = string.Empty;
            return;
        }

        var state = AppServices.Coordinator.Peers.GetState(selected.DeviceId);
        var blocked = state == PeerLinkState.ManuallyDisconnected;
        SelectButton.IsEnabled = selected.IsOnline && !blocked;
        SelectFolderButton.IsEnabled = selected.IsOnline && !blocked;
        PeerConnectionButton.IsEnabled = true;
        PeerConnectionButton.Content = blocked ? "다시 연결" : "연결 끊기";
        PeerConnectionHint.Text = state switch
        {
            PeerLinkState.ManuallyDisconnected => $"{selected.Name}와의 연결을 끊었습니다. 신뢰 기기 등록은 그대로 유지되며, 다시 연결하면 중단된 지점부터 이어집니다.",
            PeerLinkState.Connected => $"{selected.Name}와 연결되어 있습니다.",
            _ => string.Empty
        };
    }

    private async void PeerConnection_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceListItem selected) return;

        if (AppServices.Coordinator.Peers.IsManuallyDisconnected(selected.DeviceId))
        {
            await AppServices.Coordinator.ReconnectPeerAsync(selected.DeviceId);
            UpdatePeerConnectionControls();
            await RefreshHistoryAsync();
            return;
        }

        if (AppServices.Coordinator.HasActiveTransfer)
        {
            var confirm = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = $"{selected.Name}와의 연결을 끊으시겠습니까?",
                Content = "전송이 진행 중입니다. 연결을 끊어도 파일은 손상되지 않고, 안전하게 저장된 지점까지 유지됩니다. 다시 연결하면 그 지점부터 이어서 전송합니다.",
                PrimaryButtonText = "연결 끊기",
                CloseButtonText = "계속 전송",
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }

        await AppServices.Coordinator.DisconnectPeerAsync(selected.DeviceId);
        UpdatePeerConnectionControls();
        await RefreshHistoryAsync();
    }

    private async void RenameDevice_Click(object sender, RoutedEventArgs e)
    {
        var input = new TextBox
        {
            Text = AppServices.DeviceName.Current,
            MaxLength = Core.Devices.DeviceNameRules.MaxLength,
            MinWidth = 320,
            SelectionStart = AppServices.DeviceName.Current.Length
        };
        var error = new TextBlock { Visibility = Visibility.Collapsed, FontSize = 12 };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "기기 이름 변경",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"주변 기기 목록에 표시할 이름입니다. 한글을 사용할 수 있고 최대 {Core.Devices.DeviceNameRules.MaxLength}자입니다. 이름을 바꿔도 기기 인증과 신뢰 관계는 그대로 유지됩니다.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    input,
                    error
                }
            },
            PrimaryButtonText = "저장",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var result = await AppServices.DeviceName.SetAsync(input.Text);
                if (!result.Accepted)
                {
                    args.Cancel = true;
                    error.Text = result.Error ?? "기기 이름을 저장하지 못했습니다.";
                    error.Visibility = Visibility.Visible;
                    return;
                }
                AppServices.Log.Info("device.name.changed", new { name = result.Name, identityPreserved = true });
            }
            finally
            {
                deferral.Complete();
            }
        };
        await dialog.ShowAsync();
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var autoStart = new CheckBox { Content = "Windows 시작 시 자동 실행" };
        try { autoStart.IsChecked = AppServices.AutoStart.IsEnabled(); }
        catch (Exception ex) { AppServices.Log.Warn("autostart.read.failed", new { error = ex.GetType().Name }); }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "설정",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = $"이 PC 이름\n{AppServices.DeviceName.Current}", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"받은 파일 위치\n{AppServices.ReceiveDirectory}", TextWrapping = TextWrapping.Wrap },
                    autoStart,
                    new TextBlock
                    {
                        Text = "자동 실행 시 창 없이 시스템 트레이에서 시작합니다.\n창의 X 버튼은 종료가 아니라 트레이로 숨기기입니다.\n완전히 종료하려면 트레이 메뉴의 종료를 사용하세요.",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        Opacity = 0.7
                    },
                    new TextBlock { Text = "자동 이어받기: 켜짐 · 전송 중 절전 방지: 켜짐 · 프로토콜 v1", FontSize = 12, Opacity = 0.7 }
                }
            },
            PrimaryButtonText = "저장",
            CloseButtonText = "닫기",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            var enabled = autoStart.IsChecked == true;
            AppServices.AutoStart.SetEnabled(enabled);
            AppServices.Log.Info("autostart.changed", new { enabled });
        }
        catch (Exception ex)
        {
            AppServices.Log.Error("autostart.write.failed", ex);
            await ShowMessageAsync("자동 실행 설정 실패", $"자동 실행 설정을 저장하지 못했습니다.\n{ex.Message}");
        }
    }

    private Task RefreshDevicesAsync()
    {
        var selectedId = (DeviceList.SelectedItem as DeviceListItem)?.DeviceId;
        _devices.Clear();
        foreach (var device in AppServices.Discovery.Devices.OrderBy(static d => d.Name))
            _devices.Add(new DeviceListItem(device.DeviceId, device.Name, device.IsOnline ? "온라인" : "오프라인 - 자동 대기 중", device.IsOnline));
        DeviceList.SelectedItem = _devices.FirstOrDefault(d => d.DeviceId == selectedId)
            ?? _devices.Where(static device => device.IsOnline).Take(2).ToArray() switch
            {
                [var only] => only,
                _ => null
            };
        UpdatePeerConnectionControls();
        if (!AppServices.Coordinator.HasActiveTransfer) UpdateIdleStatus();
        return Task.CompletedTask;
    }

    private void RootDropSurface_DragEnter(object sender, DragEventArgs e) => UpdateDragFeedback(e);
    private void RootDropSurface_DragOver(object sender, DragEventArgs e) => UpdateDragFeedback(e);

    private void RootDropSurface_DragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private async void RootDropSurface_Drop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var storageItems = await e.DataView.GetStorageItemsAsync();
            var paths = storageItems
                .Where(static item => item is StorageFile or StorageFolder)
                .Select(static item => item.Path)
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var inaccessibleItems = storageItems.Count - paths.Length;
            if (paths.Length == 0)
            {
                await ShowMessageAsync("전송할 수 없는 항목", "Windows Explorer의 일반 파일 또는 폴더를 놓아 주세요.");
                return;
            }

            var target = await ResolveDropTargetAsync();
            if (target is null) return;

            AppServices.Log.Info("drag_drop.queue.begin", new
            {
                target.DeviceId,
                itemCount = paths.Length,
                inaccessibleItems
            });
            var result = await AppServices.Coordinator.QueueFilesAsync(target.DeviceId, paths);
            AppServices.Log.Info("drag_drop.queue.complete", new
            {
                target.DeviceId,
                result.QueuedFiles,
                result.SkippedItems,
                inaccessibleItems
            });
            if (result.QueuedFiles == 0)
            {
                await ShowMessageAsync("전송할 파일이 없습니다", "놓은 항목에서 접근 가능한 일반 파일을 찾지 못했습니다.");
                return;
            }
            if (inaccessibleItems > 0)
                await ShowMessageAsync("일부 항목을 건너뜀", $"경로에 접근할 수 없는 항목 {inaccessibleItems}개를 제외하고 전송을 시작했습니다.");
            await ShowSkippedItemsIfNeededAsync(result);
        }
        catch (Exception ex)
        {
            AppServices.Log.Error("drag_drop.failed", ex);
            await ShowMessageAsync("드래그앤드롭 실패", $"놓은 항목을 전송 대기열에 추가하지 못했습니다.\n{ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void UpdateDragFeedback(DragEventArgs e)
    {
        var hasStorageItems = e.DataView.Contains(StandardDataFormats.StorageItems);
        var online = _devices.Where(static device => device.IsOnline).ToArray();
        e.AcceptedOperation = hasStorageItems && online.Length > 0
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
        if (e.AcceptedOperation == DataPackageOperation.Copy)
        {
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.Caption = "QuickSend로 전송";
            DropOverlayCaption.Text = DeviceList.SelectedItem is DeviceListItem { IsOnline: true } selected
                ? $"{selected.Name}(으)로 전송합니다"
                : online.Length == 1
                    ? $"{online[0].Name}(으)로 전송합니다"
                    : "놓은 뒤 전송할 기기를 선택합니다";
            DropOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            DropOverlay.Visibility = Visibility.Collapsed;
        }
        e.Handled = true;
    }

    private async Task<DeviceListItem?> ResolveDropTargetAsync()
    {
        if (DeviceList.SelectedItem is DeviceListItem { IsOnline: true } selected) return selected;
        var online = _devices.Where(static device => device.IsOnline).ToArray();
        if (online.Length == 0)
        {
            await ShowMessageAsync("온라인 기기가 없습니다", "받을 기기에서 QuickSend를 실행한 뒤 다시 놓아 주세요.");
            return null;
        }
        if (online.Length == 1)
        {
            DeviceList.SelectedItem = online[0];
            return online[0];
        }

        var picker = new ComboBox
        {
            ItemsSource = online,
            DisplayMemberPath = nameof(DeviceListItem.Name),
            SelectedIndex = 0,
            MinWidth = 320
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "전송할 기기 선택",
            Content = picker,
            PrimaryButtonText = "전송",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        var target = picker.SelectedItem as DeviceListItem;
        DeviceList.SelectedItem = target;
        return target;
    }

    private async Task ShowSkippedItemsIfNeededAsync(QueueFilesResult result)
    {
        if (result.SkippedItems == 0 || result.QueuedFiles == 0) return;
        var reasons = string.Join("\n", result.Errors.Distinct().Take(3).Select(static reason => $"• {reason}"));
        await ShowMessageAsync(
            "일부 항목을 건너뜀",
            $"접근할 수 없거나 지원하지 않는 항목 {result.SkippedItems}개를 제외하고 {result.QueuedFiles}개 파일을 대기열에 추가했습니다.\n\n{reasons}");
    }

    private void UpdateIdleStatus()
    {
        TransferMetricsPanel.Visibility = Visibility.Collapsed;
        PauseButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        if (DeviceList.SelectedItem is DeviceListItem { IsOnline: true } selected)
            StatusText.Text = $"{selected.Name}에 연결할 준비가 되었습니다";
        else if (_devices.Any(static device => device.IsOnline))
            StatusText.Text = "전송할 기기와 파일을 선택하세요";
        else
            StatusText.Text = "주변 기기를 찾는 중...";
    }

    private async Task RefreshHistoryAsync()
    {
        var running = AppServices.Coordinator.RunningTransferIds;
        var queue = AppServices.Coordinator.QueuePositions;
        var presentation = new HistoryPresentation(
            transferId => queue.TryGetValue(transferId, out var position) ? position : null,
            AppServices.Coordinator.IsDestinationOnline);
        _history.Clear();
        await foreach (var item in AppServices.History.GetRecentAsync(running, presentation))
            _history.Add(item);
    }

    private async Task ShowMessageAsync(string title, string message) => await new ContentDialog
    {
        XamlRoot = Content.XamlRoot,
        Title = title,
        Content = message,
        CloseButtonText = "확인"
    }.ShowAsync();

    /// <summary>
    /// The X button hides the window; it is not an exit. The listener, mDNS advertisement
    /// and any running transfer stay alive so the peer can still find this PC and send to
    /// it. Only the tray menu's 종료 sets <see cref="_allowClose"/> and really exits.
    /// </summary>
    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        var hasTransfer = AppServices.Coordinator.HasActiveTransfer;
        AppServices.Log.Info("window.closing", new { _allowClose, hasTransfer });
        if (_allowClose) return;
        args.Cancel = true;
        sender.Hide();
        if (hasTransfer) _tray?.ShowTransferContinuesNotice();
        AppServices.Log.Info("window.hidden.to_tray", new { hasTransfer, listenerRetained = true });
    }

    private void Tray_OpenRequested(object? sender, EventArgs e)
    {
        AppServices.Log.Info("tray.open.callback.enter");
        QueueTrayUi("tray-open", () => ShowAndActivateCore("tray-open"));
    }

    private void Tray_PauseRequested(object? sender, EventArgs e)
    {
        AppServices.Log.Info("tray.pause.callback.enter");
        QueueTrayUi("tray-pause", AppServices.Coordinator.TogglePause);
    }

    private void Tray_ExitRequested(object? sender, EventArgs e)
    {
        AppServices.Log.Info("tray.exit.callback.enter");
        QueueTrayUiAsync("tray-exit", async () =>
        {
            if (AppServices.Coordinator.HasActiveTransfer)
            {
                ShowAndActivateCore("tray-exit-confirmation");
                var dialog = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    Title = "전송 중에 종료하시겠습니까?",
                    Content = "안전하게 저장된 지점까지는 다음 실행 때 이어받을 수 있습니다.",
                    PrimaryButtonText = "종료",
                    CloseButtonText = "계속 전송",
                    DefaultButton = ContentDialogButton.Close
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            }
            if (Application.Current is App app)
                await app.RequestExitFromTrayAsync();
        });
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        _closed = true;
        AppServices.Log.Info("window.closed");
        AppServices.Discovery.DeviceChanged -= Discovery_DeviceChanged;
        AppServices.Coordinator.ProgressChanged -= Coordinator_ProgressChanged;
        AppServices.Coordinator.PairingRequested -= Coordinator_PairingRequested;
        AppServices.Coordinator.Peers.Changed -= Coordinator_PeerConnectionChanged;
        AppServices.DeviceName.NameChanged -= DeviceName_Changed;
        AppServices.TransferStore.Changed -= Store_Changed;
        AppServices.History.Changed -= Store_Changed;
        AppServices.Coordinator.RunningTransfersChanged -= Store_Changed;
        AppWindow.Closing -= AppWindow_Closing;
        if (_tray is not null)
        {
            _tray.OpenRequested -= Tray_OpenRequested;
            _tray.PauseRequested -= Tray_PauseRequested;
            _tray.ExitRequested -= Tray_ExitRequested;
            _tray.Dispose();
            _tray = null;
        }
        if (Application.Current is App app)
            app.OnMainWindowClosed(this);
    }

    private void EnqueueUi(string operation, Action action)
    {
        if (_closed) return;
        if (_uiDispatcher.HasThreadAccess)
        {
            action();
            return;
        }
        var queued = _uiDispatcher.TryEnqueue(() =>
        {
            try { action(); }
            catch (Exception ex) { AppServices.Log.Error($"ui.dispatch.{operation}.failed", ex); }
        });
        AppServices.Log.Info("ui.dispatch.enqueue", new { operation, queued });
        if (!queued) AppServices.Log.Warn("ui.dispatch.enqueue.failed", new { operation });
    }

    private void QueueTrayUi(string operation, Action action)
    {
        if (_closed) return;
        var queued = _uiDispatcher.TryEnqueue(() =>
        {
            try { action(); }
            catch (Exception ex) { AppServices.Log.Error($"ui.dispatch.{operation}.failed", ex); }
        });
        AppServices.Log.Info("ui.dispatch.enqueue", new { operation, queued, source = "tray" });
        if (!queued) AppServices.Log.Warn("ui.dispatch.enqueue.failed", new { operation, source = "tray" });
    }

    private void QueueTrayUiAsync(string operation, Func<Task> action)
    {
        if (_closed) return;
        var queued = _uiDispatcher.TryEnqueue(async () =>
        {
            try { await action(); }
            catch (Exception ex) { AppServices.Log.Error($"ui.dispatch.{operation}.failed", ex); }
        });
        AppServices.Log.Info("ui.dispatch.enqueue", new { operation, queued, source = "tray" });
        if (!queued) AppServices.Log.Warn("ui.dispatch.enqueue.failed", new { operation, source = "tray" });
    }

    private Task EnqueueUiAsync(string operation, Func<Task> action)
    {
        if (_closed) return Task.CompletedTask;
        if (_uiDispatcher.HasThreadAccess) return action();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _uiDispatcher.TryEnqueue(async () =>
        {
            try { await action(); completion.TrySetResult(); }
            catch (Exception ex) { AppServices.Log.Error($"ui.dispatch.{operation}.failed", ex); completion.TrySetException(ex); }
        });
        AppServices.Log.Info("ui.dispatch.enqueue", new { operation, queued });
        if (!queued) completion.TrySetException(new InvalidOperationException($"UI dispatcher rejected {operation}."));
        return completion.Task;
    }

    private void EnqueueUiAsyncFireAndForget(string operation, Func<Task> action) =>
        _ = EnqueueUiAsync(operation, action).ContinueWith(task =>
            AppServices.Log.Error($"ui.dispatch.{operation}.failed", task.Exception!.GetBaseException()),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    private void LogWindowState(string stage)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        AppServices.Log.Info("window.state", new
        {
            stage,
            hwnd = $"0x{hwnd.ToInt64():X}",
            visible = IsWindowVisible(hwnd),
            appWindowId = AppWindow.Id.Value,
            presenterState = (AppWindow.Presenter as OverlappedPresenter)?.State.ToString()
        });
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var amount = (double)Math.Max(value, 0);
        var unit = 0;
        while (amount >= 1024 && unit < units.Length - 1) { amount /= 1024; unit++; }
        return $"{amount:0.#} {units[unit]}";
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);
}

public sealed record DeviceListItem(string DeviceId, string Name, string StatusText, bool IsOnline);
