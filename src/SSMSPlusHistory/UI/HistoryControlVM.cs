namespace SSMSPlusHistory.UI.VM
{
    using SSMSPlusCore.App;
    using SSMSPlusCore.Integration;
    using SSMSPlusCore.Ui;
    using SSMSPlusCore.Utils;

    using SSMSPlusHistory.Entities.Search;
    using SSMSPlusHistory.Repositories;
    using SSMSPlusHistory.Services;
    using SSMSPlusHistory.Services.Filtering;

    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;

    public class HistoryControlVM : ViewModelBase
    {
        private QueryItemRepository _itemsRepository;
        private IVersionProvider _versionProvider;
        private IServiceCacheIntegration _serviceCacheIntegration;

        public IAsyncCommand RequestItemsCommand { get; private set; }
        public IAsyncCommand ViewLoadedCommand { get; private set; }
        public Command<SearchFilterResultVM> OpenScriptCmd { get; }

        public Command<SearchFilterResultVM> DeleteCmd { get; }

        private bool _loadedOnce = false;

        public HistoryControlVM(QueryItemRepository itemsRepository, IVersionProvider versionProvider, IServiceCacheIntegration serviceCacheIntegration)
        {
            _itemsRepository = itemsRepository;
            _versionProvider = versionProvider;
            _serviceCacheIntegration = serviceCacheIntegration;
            RequestItemsCommand = new AsyncCommand(FuncHelper.DebounceAsync(ExecuteRequestItemsAsync, 100), CanExecuteSubmit, this.HandleError);
            ViewLoadedCommand = new AsyncCommand(OnViewLoadedAsync, CanExecuteSubmit, this.HandleError);
            OpenScriptCmd = new Command<SearchFilterResultVM>(OpenScript, () => true, HandleError);
            DeleteCmd = new Command<SearchFilterResultVM>(item => _ = DeleteAsync(item), () => true, HandleError);
            InitDefaults();
            QueryTracker.ItemsSaved += OnItemsSaved;
        }

        /// <summary>New queries were saved: refresh the list if the window has been opened.</summary>
        private void OnItemsSaved(object sender, EventArgs e)
        {
            if (!_loadedOnce)
                return;

            _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!IsLoading)
                    await RequestItemsCommand.ExecuteAsync();
            });
        }

        private void OpenScript(SearchFilterResultVM arg)
        {
            var item = arg.SearchResult.QueryItem;
            _serviceCacheIntegration.OpenScriptInNewWindow(item.Query, item.Server, item.Database);
        }

        /// <summary>Deletes the history entry (right click > Delete, or the Delete key) and removes it from the list.</summary>
        private async Task DeleteAsync(SearchFilterResultVM arg)
        {
            var item = arg?.SearchResult?.QueryItem;
            if (item == null)
                return;

            try
            {
                await _itemsRepository.Delete(item.Id);
                int index = QueryItemsVM?.IndexOf(arg) ?? -1;
                QueryItemsVM = QueryItemsVM?.Where(v => !ReferenceEquals(v, arg)).ToList();
                if (QueryItemsVM != null && QueryItemsVM.Count > 0 && index >= 0)
                    SelectedItem = QueryItemsVM[Math.Min(index, QueryItemsVM.Count - 1)];
                Message = $"{QueryItemsVM?.Count ?? 0} Result(s) - 1 deleted";
            }
            catch (Exception ex)
            {
                HandleError(ex);
            }
        }

        private bool CanExecuteSubmit()
        {
            return !IsLoading;
        }

        private async Task OnViewLoadedAsync()
        {
            if (_loadedOnce)
                return;

            await RequestItemsCommand.ExecuteAsync();
            _loadedOnce = true;
        }

        private async Task ExecuteRequestItemsAsync()
        {
            try
            {
                IsLoading = true;

                var sw = System.Diagnostics.Stopwatch.StartNew();

                // The default end date follows the clock, so a window left open since yesterday still shows today's queries.
                if (!_endDateChanged && _endDate != DateTime.Now.AddDays(1).Date)
                    SetField(ref _endDate, DateTime.Now.AddDays(1).Date, nameof(EndDate));

                var filterContext = new FilterContext(QueryFilter, ServerFilter, DbFilter, StartDate.ToUniversalTime(), EndDate.ToUniversalTime());
                var result = await _itemsRepository.FindItems(filterContext);
                QueryItemsVM = result.Select(q => new ScriptSearchTarget(q)).Select(p => new SearchFilterResultVM(p, filterContext)).ToList();

                Message = $"{QueryItemsVM.Count} Result(s) in {sw.ElapsedMilliseconds} ms";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void HandleError(Exception ex)
        {
            Message = ex.Message;
        }

        private void InitDefaults()
        {
            _endDate = DateTime.Now.AddDays(1).Date;
            _startDate = EndDate.AddDays(-60).Date;
        }

        private List<SearchFilterResultVM> _queryItemsVM;
        public List<SearchFilterResultVM> QueryItemsVM
        {
            get => _queryItemsVM;
            private set => SetField(ref _queryItemsVM, value);
        }

        private string _message = "Click on the button to load data";
        public string Message
        {
            get => _message;
            set => SetField(ref _message, value);
        }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set => SetField(ref _isLoading, value);
        }

        private DateTime _startDate;
        public DateTime StartDate
        {
            get => _startDate;
            set
            {
                SetField(ref _startDate, value);
                OnSearchChange();
            }
        }

        private DateTime _endDate;
        private bool _endDateChanged;
        public DateTime EndDate
        {
            get => _endDate;
            set
            {
                if (value != _endDate)
                    _endDateChanged = true;
                SetField(ref _endDate, value);
                OnSearchChange();
            }
        }

        private string _queryFilter;
        public string QueryFilter
        {
            get => _queryFilter;
            set
            {
                SetField(ref _queryFilter, value);
                OnSearchChange();
            }
        }

        private string _dbFilter;
        public string DbFilter
        {
            get => _dbFilter;
            set
            {
                SetField(ref _dbFilter, value);
                OnSearchChange();
            }
        }

        private string _serverFilter;
        public string ServerFilter
        {
            get => _serverFilter;
            set
            {
                SetField(ref _serverFilter, value);
                OnSearchChange();
            }
        }

        private SearchFilterResultVM _selectedItem;
        public SearchFilterResultVM SelectedItem
        {
            get => _selectedItem;
            set
            {
                SetField(ref _selectedItem, value);
            }
        }

        private void OnSearchChange()
        {
            RequestItemsCommand.ExecuteAsync();
        }
    }
}
