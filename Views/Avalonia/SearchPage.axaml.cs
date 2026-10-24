using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SLSKDONET.ViewModels;
using SLSKDONET.Views.Avalonia.Controls;

namespace SLSKDONET.Views.Avalonia
{
    public partial class SearchPage : UserControl
    {
        public SearchPage()
        {
            InitializeComponent();

            var grid = this.FindControl<VirtualGrid>("ResultsGrid");
            if (grid != null)
            {
                // Double-click / Enter on a row downloads the selection.
                grid.DoubleTapped += (_, _) => DownloadSelection();
                grid.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter && DownloadSelection())
                        e.Handled = true;
                };

                // VirtualGrid owns selection; mirror it into the view model (single handler).
                grid.SelectionChanged += OnResultsSelectionChanged;
            }

            // Enter searches, Esc stops a running search. Tunnel so the TextBox can't swallow them.
            foreach (var name in new[] { "SearchQueryTextBox", "PeerUsernameTextBox" })
            {
                this.FindControl<TextBox>(name)?.AddHandler(KeyDownEvent, OnSearchBoxKeyDown, RoutingStrategies.Tunnel);
            }

            // Drag a CSV onto the page to put its path in the search box.
            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);
        }

        public SearchPage(SearchViewModel viewModel) : this()
        {
            DataContext = viewModel;
            viewModel.FocusRequested += () => global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                this.FindControl<TextBox>("SearchQueryTextBox")?.Focus();
            });
        }

        private bool DownloadSelection()
        {
            if (DataContext is not SearchViewModel vm || !vm.SelectedResults.Any())
                return false;
            // ReactiveCommand.Execute is cold: without Subscribe nothing runs (this used to be a
            // dead double-click). Failures surface through the command's ThrownExceptions.
            vm.DownloadSelectedCommand.Execute(null).Subscribe(_ => { }, _ => { });
            return true;
        }

        private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
        {
            if (DataContext is not SearchViewModel vm) return;

            if (e.Key == Key.Enter && vm.UnifiedSearchCommand.CanExecute(null))
            {
                vm.UnifiedSearchCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && vm.IsSearching)
            {
                vm.CancelSearchCommand.Execute(null);
                e.Handled = true;
            }
        }

        private void OnResultsSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (DataContext is not SearchViewModel vm) return;

            foreach (var item in e.RemovedItems.OfType<AnalyzedSearchResultViewModel>())
                vm.SelectedResults.Remove(item);

            foreach (var item in e.AddedItems.OfType<AnalyzedSearchResultViewModel>())
            {
                if (!vm.SelectedResults.Contains(item))
                    vm.SelectedResults.Add(item);
            }
        }

        private void OnDragOver(object? sender, DragEventArgs e)
        {
            var files = e.Data.Contains(DataFormats.Files) ? e.Data.GetFiles() : null;
            e.DragEffects = files != null && files.Any(f => f.Name.EndsWith(".csv", System.StringComparison.OrdinalIgnoreCase))
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        }

        private void OnDrop(object? sender, DragEventArgs e)
        {
            if (!e.Data.Contains(DataFormats.Files)) return;

            var csvFile = e.Data.GetFiles()?.FirstOrDefault(f => f.Name.EndsWith(".csv", System.StringComparison.OrdinalIgnoreCase));
            if (csvFile == null || DataContext is not SearchViewModel vm) return;

            // The import provider is picked from the path when the user presses Enter / Search.
            vm.SearchQuery = csvFile.Path.IsAbsoluteUri && csvFile.Path.Scheme == "file"
                ? csvFile.Path.LocalPath
                : System.Uri.UnescapeDataString(csvFile.Path.ToString());
            this.FindControl<TextBox>("SearchQueryTextBox")?.Focus();
        }
    }
}
