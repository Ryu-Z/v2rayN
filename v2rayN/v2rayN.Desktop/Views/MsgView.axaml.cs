using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

public partial class MsgView : ReactiveUserControl<MsgViewModel>
{
    //private const int KeepLines = 30;

    public MsgView()
    {
        InitializeComponent();
        txtMsg.TextArea.TextView.Options.EnableHyperlinks = false;
        ViewModel = new MsgViewModel(UpdateViewHandler);

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.FilterTypes, v => v.cmbMsgFilterType.ItemsSource).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedMsgFilterType, v => v.cmbMsgFilterType.SelectedItem).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.RegularMsgFilter, v => v.txtRegularMsgFilter.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.GeoSiteSearchText, v => v.txtGeoSiteSearch.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.IsRegularFilterMode, v => v.txtRegularMsgFilter.IsVisible).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.IsGeoSiteMode, v => v.btnGeoSiteSelector.IsVisible).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.FilteredGeoSiteItems, v => v.lstGeoSiteItems.ItemsSource).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.GeoSiteSelectionSummary, v => v.btnGeoSiteSelector.Content).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.GeoSiteSelectionCountText, v => v.txtGeoSiteCount.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.AutoRefresh, v => v.togAutoRefresh.IsChecked).DisposeWith(disposables);
        });

        TextEditorKeywordHighlighter.Attach(txtMsg, Global.LogLevelColors.ToDictionary(
                kv => kv.Key,
                kv => (IBrush)new SolidColorBrush(Color.Parse(kv.Value))
            ));
    }

    private async Task<bool> UpdateViewHandler(EViewAction action, object? obj)
    {
        switch (action)
        {
            case EViewAction.DispatcherShowMsg:
                if (obj is null)
                {
                    return false;
                }

                Dispatcher.UIThread.Post(() => ShowMsg(obj),
                    DispatcherPriority.ApplicationIdle);
                break;
        }
        return await Task.FromResult(true);
    }

    private void ShowMsg(object msg)
    {
        //var lineCount = txtMsg.LineCount;
        //if (lineCount > ViewModel?.NumMaxMsg)
        //{
        //    var cutLine = txtMsg.Document.GetLineByNumber(lineCount - KeepLines);
        //    txtMsg.Document.Remove(0, cutLine.Offset);
        //}
        if (txtMsg.LineCount > ViewModel?.NumMaxMsg)
        {
            ClearMsg();
        }

        txtMsg.AppendText(msg.ToString());
        if (togScrollToEnd.IsChecked ?? true)
        {
            txtMsg.ScrollToEnd();
        }
    }

    public void ClearMsg()
    {
        txtMsg.Clear();
        txtMsg.AppendText("----- Message cleared -----\n");
    }

    private void menuMsgViewSelectAll_Click(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            txtMsg.TextArea.Focus();
            txtMsg.SelectAll();
        }, DispatcherPriority.Render);
    }

    private async void menuMsgViewCopy_Click(object? sender, RoutedEventArgs e)
    {
        var data = txtMsg.SelectedText.TrimEx();
        await AvaUtils.SetClipboardData(this, data);
    }

    private async void menuMsgViewCopyAll_Click(object? sender, RoutedEventArgs e)
    {
        var data = txtMsg.Text.TrimEx();
        await AvaUtils.SetClipboardData(this, data);
    }

    private void menuMsgViewClear_Click(object? sender, RoutedEventArgs e)
    {
        ClearMsg();
    }

    private void btnGeoSiteSelector_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.RefreshGeoSiteItems();
    }

    private void btnClearGeoSite_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.ClearGeoSiteSelection();
    }
}
