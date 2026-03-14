using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace SLSKDONET.Helpers;

/// <summary>
/// An ObservableCollection that supports incremental loading.
/// This acts as a bridge between the Avalonia UI and the ViewModel's data paging logic.
/// </summary>
/// <typeparam name="T">The type of items in the collection.</typeparam>
public class IncrementalObservableCollection<T> : ObservableCollection<T>, ISupportIncrementalLoading
{
    private readonly Func<uint, Task<IEnumerable<T>>> _loadMoreItems;
    private bool _hasMoreItems = true;
    private bool _isLoading;

    public bool HasMoreItems
    {
        get => _hasMoreItems;
        private set
        {
            if (_hasMoreItems != value)
            {
                _hasMoreItems = value;
                OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasMoreItems)));
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading != value)
            {
                _isLoading = value;
                OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsLoading)));
            }
        }
    }

    public IncrementalObservableCollection(Func<uint, Task<IEnumerable<T>>> loadMoreItems)
    {
        _loadMoreItems = loadMoreItems ?? throw new ArgumentNullException(nameof(loadMoreItems));
    }

    public async Task<int> LoadMoreItemsAsync(uint count)
    {
        if (IsLoading || !HasMoreItems) return 0;

        IsLoading = true;
        try
        {
            var items = await _loadMoreItems(count);
            var itemList = items?.ToList() ?? new List<T>();

            if (itemList.Any())
            {
                foreach (var item in itemList)
                {
                    Add(item);
                }
            }
            else
            {
                HasMoreItems = false;
            }

            return itemList.Count;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
