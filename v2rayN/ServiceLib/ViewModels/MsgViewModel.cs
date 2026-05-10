namespace ServiceLib.ViewModels;

public class GeoSiteFilterItem : MyReactiveObject
{
    public GeoSiteFilterItem(string name, bool isSelected)
    {
        Name = name;
        DisplayName = name;
        IsSelected = isSelected;
    }

    public string Name { get; }
    public string DisplayName { get; }

    [Reactive]
    public bool IsSelected { get; set; }
}

public class MsgViewModel : MyReactiveObject
{
    private const string FilterTypeGeoSite = "geosite";
    private const string FilterTypeRegular = "常规筛选";

    private readonly ConcurrentQueue<string> _queueMsg = new();
    private readonly CompositeDisposable _geoSiteSelectionDisposables = new();
    private List<GeoSiteFilterItem> _allGeoSiteItems = [];
    private bool _isInitializing;
    private volatile bool _lastMsgFilterNotAvailable;
    private int _showLock = 0; // 0 = unlocked, 1 = locked
    public int NumMaxMsg { get; } = 500;

    public IObservableCollection<GeoSiteFilterItem> FilteredGeoSiteItems { get; } = new ObservableCollectionExtended<GeoSiteFilterItem>();

    [Reactive]
    public string SelectedMsgFilterType { get; set; }

    [Reactive]
    public int SelectedMsgFilterIndex { get; set; }

    [Reactive]
    public string RegularMsgFilter { get; set; }

    [Reactive]
    public string GeoSiteSearchText { get; set; }

    [Reactive]
    public string GeoSiteSelectionSummary { get; set; }

    [Reactive]
    public string GeoSiteSelectionCountText { get; set; }

    [Reactive]
    public bool IsGeoSiteMode { get; set; }

    [Reactive]
    public bool IsRegularFilterMode { get; set; }

    [Reactive]
    public bool AutoRefresh { get; set; }

    public MsgViewModel(Func<EViewAction, object?, Task<bool>>? updateView)
    {
        _config = AppManager.Instance.Config;
        _updateView = updateView;
        _isInitializing = true;

        var savedFilter = _config.MsgUIItem.MainMsgFilter ?? string.Empty;
        var savedGeoSiteTags = _config.MsgUIItem.MainMsgFilterGeoSites?.ToList() ?? GeoSiteFilterService.ParseGeoSiteTags(savedFilter);
        var savedFilterType = _config.MsgUIItem.MainMsgFilterType;
        var useGeoSiteMode = savedFilterType == FilterTypeGeoSite
            || savedFilterType.IsNullOrEmpty() && (savedFilter.IsNullOrEmpty() || GeoSiteFilterService.IsGeoSiteFilter(savedFilter));

        RegularMsgFilter = useGeoSiteMode ? string.Empty : savedFilter;
        GeoSiteSearchText = string.Empty;
        GeoSiteSelectionSummary = string.Empty;
        GeoSiteSelectionCountText = string.Empty;
        LoadGeoSiteItems(savedGeoSiteTags);
        SelectedMsgFilterType = useGeoSiteMode ? FilterTypeGeoSite : FilterTypeRegular;
        SelectedMsgFilterIndex = useGeoSiteMode ? 0 : 1;
        ApplyFilterMode();

        AutoRefresh = _config.MsgUIItem.AutoRefresh ?? true;
        _isInitializing = false;
        PersistFilter();

        this.WhenAnyValue(
           x => x.SelectedMsgFilterIndex)
               .Subscribe(c => ApplyFilterMode());

        this.WhenAnyValue(
           x => x.RegularMsgFilter)
               .Subscribe(c => PersistFilter());

        this.WhenAnyValue(
           x => x.GeoSiteSearchText)
               .Subscribe(c => UpdateFilteredGeoSiteItems());

        this.WhenAnyValue(
          x => x.AutoRefresh,
          y => y == true)
              .Subscribe(c => _config.MsgUIItem.AutoRefresh = AutoRefresh);

        AppEvents.SendMsgViewRequested
         .AsObservable()
         //.ObserveOn(RxSchedulers.MainThreadScheduler)
         .Subscribe(content => _ = AppendQueueMsg(content));
    }

    private async Task AppendQueueMsg(string msg)
    {
        if (AutoRefresh == false)
        {
            return;
        }

        EnqueueQueueMsg(msg);

        if (!AppManager.Instance.ShowInTaskbar)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _showLock, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await Task.Delay(500).ConfigureAwait(false);

            var sb = new StringBuilder();
            while (_queueMsg.TryDequeue(out var line))
            {
                sb.Append(line);
            }

            await _updateView?.Invoke(EViewAction.DispatcherShowMsg, sb.ToString());
        }
        finally
        {
            Interlocked.Exchange(ref _showLock, 0);
        }
    }

    private void EnqueueQueueMsg(string msg)
    {
        var msgFilter = GetEffectiveMsgFilter();

        //filter msg
        if (msgFilter.IsNotEmpty() && !_lastMsgFilterNotAvailable)
        {
            try
            {
                if (!GeoSiteFilterService.IsMatch(msg, msgFilter))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                EnqueueWithLimit(ex.Message);
                _lastMsgFilterNotAvailable = true;
            }
        }

        EnqueueWithLimit(msg);
        if (!msg.EndsWith(Environment.NewLine))
        {
            EnqueueWithLimit(Environment.NewLine);
        }
    }

    private void EnqueueWithLimit(string item)
    {
        _queueMsg.Enqueue(item);

        while (_queueMsg.Count > NumMaxMsg)
        {
            _queueMsg.TryDequeue(out _);
        }
    }

    //public void ClearMsg()
    //{
    //    _queueMsg.Clear();
    //}

    public void RefreshGeoSiteItems()
    {
        LoadGeoSiteItems(GetSelectedGeoSiteTags());
        UpdateGeoSiteFilter();
    }

    public void ClearGeoSiteSelection()
    {
        foreach (var item in _allGeoSiteItems.Where(t => t.IsSelected))
        {
            item.IsSelected = false;
        }

        UpdateGeoSiteFilter();
    }

    private void LoadGeoSiteItems(IEnumerable<string> selectedTags)
    {
        var selectedSet = new HashSet<string>(selectedTags, StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<string> codes;
        try
        {
            codes = GeoSiteFilterService.GetGeoSiteCodes();
        }
        catch (Exception ex)
        {
            codes = [];
            GeoSiteSelectionSummary = ex.Message;
        }

        _geoSiteSelectionDisposables.Clear();
        _allGeoSiteItems = codes
            .Select(code => new GeoSiteFilterItem(code, selectedSet.Contains(code)))
            .ToList();

        foreach (var item in _allGeoSiteItems)
        {
            _geoSiteSelectionDisposables.Add(item.WhenAnyValue(x => x.IsSelected)
                .Skip(1)
                .Subscribe(_ => UpdateGeoSiteFilter()));
        }

        UpdateFilteredGeoSiteItems();
        UpdateGeoSiteFilter();
    }

    private void ApplyFilterMode()
    {
        SelectedMsgFilterType = SelectedMsgFilterIndex == 0 ? FilterTypeGeoSite : FilterTypeRegular;
        IsGeoSiteMode = SelectedMsgFilterType == FilterTypeGeoSite;
        IsRegularFilterMode = !IsGeoSiteMode;
        PersistFilter();
    }

    private void UpdateFilteredGeoSiteItems()
    {
        var keyword = GeoSiteSearchText.TrimEx();
        var items = _allGeoSiteItems
            .Where(t => keyword.IsNullOrEmpty() || t.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.IsSelected)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        FilteredGeoSiteItems.Clear();
        FilteredGeoSiteItems.AddRange(items);
        UpdateGeoSiteSelectionText();
    }

    private void UpdateGeoSiteFilter()
    {
        UpdateGeoSiteSelectionText();
        PersistFilter();
    }

    private void UpdateGeoSiteSelectionText()
    {
        var selected = GetSelectedGeoSiteTags();
        if (selected.Count == 0)
        {
            GeoSiteSelectionSummary = "选择 geosite（支持多选）";
        }
        else if (selected.Count <= 3)
        {
            GeoSiteSelectionSummary = string.Join(", ", selected);
        }
        else
        {
            GeoSiteSelectionSummary = $"{string.Join(", ", selected.Take(3))} +{selected.Count - 3}";
        }

        var countText = selected.Count == 0
            ? $"可选 {_allGeoSiteItems.Count} 项"
            : $"已选择 {selected.Count} 项 / 可选 {_allGeoSiteItems.Count} 项";
        if (GeoSiteSearchText.IsNotEmpty())
        {
            countText = $"{countText}，匹配 {FilteredGeoSiteItems.Count} 项";
        }
        GeoSiteSelectionCountText = countText;
    }

    private List<string> GetSelectedGeoSiteTags()
    {
        return _allGeoSiteItems
            .Where(t => t.IsSelected)
            .Select(t => t.Name)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string GetEffectiveMsgFilter()
    {
        if (SelectedMsgFilterType == FilterTypeGeoSite)
        {
            return GeoSiteFilterService.BuildGeoSiteFilter(GetSelectedGeoSiteTags());
        }

        return RegularMsgFilter ?? string.Empty;
    }

    private void PersistFilter()
    {
        if (_isInitializing || _config is null)
        {
            return;
        }

        _config.MsgUIItem.MainMsgFilterType = SelectedMsgFilterType;
        _config.MsgUIItem.MainMsgFilter = GetEffectiveMsgFilter();
        _config.MsgUIItem.MainMsgFilterGeoSites = GetSelectedGeoSiteTags();
        _lastMsgFilterNotAvailable = false;
    }
}
