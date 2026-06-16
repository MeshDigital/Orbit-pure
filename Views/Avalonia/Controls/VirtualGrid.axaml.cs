using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using SLSKDONET.Helpers;

namespace SLSKDONET.Views.Avalonia.Controls
{
    public partial class VirtualGrid : UserControl
    {
        public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
            AvaloniaProperty.Register<VirtualGrid, IEnumerable?>(nameof(ItemsSource));

        public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty =
            AvaloniaProperty.Register<VirtualGrid, IDataTemplate?>(nameof(ItemTemplate));

        public static readonly StyledProperty<SelectionMode> SelectionModeProperty =
            AvaloniaProperty.Register<VirtualGrid, SelectionMode>(nameof(SelectionMode), SelectionMode.Multiple);

        public static readonly RoutedEvent<SelectionChangedEventArgs> SelectionChangedEvent =
            RoutedEvent.Register<VirtualGrid, SelectionChangedEventArgs>(nameof(SelectionChanged), RoutingStrategies.Bubble);

        public IEnumerable? ItemsSource
        {
            get => GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        public IDataTemplate? ItemTemplate
        {
            get => GetValue(ItemTemplateProperty);
            set => SetValue(ItemTemplateProperty, value);
        }

        public SelectionMode SelectionMode
        {
            get => GetValue(SelectionModeProperty);
            set => SetValue(SelectionModeProperty, value);
        }

        public event EventHandler<SelectionChangedEventArgs>? SelectionChanged
        {
            add => AddHandler(SelectionChangedEvent, value);
            remove => RemoveHandler(SelectionChangedEvent, value);
        }

        private readonly List<object> _selectedItems = new();
        public IList SelectedItems => _selectedItems;
        public object? SelectedItem => _selectedItems.FirstOrDefault();

        private int _selectionAnchorIndex = -1;
        private int _focusedIndex = -1;
        private bool _isUpdatingSelection = false;
        private bool _isIncrementalLoading = false;

        public VirtualGrid()
        {
            InitializeComponent();

            PartItemsRepeater.ElementPrepared += OnElementPrepared;
            PartItemsRepeater.ElementClearing += OnElementClearing;
            PartScrollViewer.ScrollChanged += OnScrollViewerScrollChanged;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == ItemsSourceProperty)
            {
                UnsubscribeFromItemsSource(change.OldValue as IEnumerable);
                _selectedItems.Clear();
                _selectionAnchorIndex = -1;
                _focusedIndex = -1;

                var newSource = change.NewValue as IEnumerable;
                SubscribeToItemsSource(newSource);

                PartItemsRepeater.ItemsSource = newSource;

                // Sync initial selections from the source
                if (newSource != null)
                {
                    foreach (var item in newSource)
                    {
                        if (item == null) continue;
                        var prop = item.GetType().GetProperty("IsSelected");
                        if (prop != null && prop.PropertyType == typeof(bool))
                        {
                            try
                            {
                                var val = (bool)prop.GetValue(item)!;
                                if (val && !_selectedItems.Contains(item))
                                {
                                    _selectedItems.Add(item);
                                }
                            }
                            catch { }
                        }
                    }
                }
                UpdateVisualStates();
            }
            else if (change.Property == ItemTemplateProperty)
            {
                PartItemsRepeater.ItemTemplate = change.NewValue as IDataTemplate;
            }
        }

        private void SubscribeToItemsSource(IEnumerable? source)
        {
            if (source is INotifyCollectionChanged ncc)
            {
                ncc.CollectionChanged += OnItemsSourceCollectionChanged;
            }
            SubscribeToItemProperties(source);
        }

        private void UnsubscribeFromItemsSource(IEnumerable? source)
        {
            if (source is INotifyCollectionChanged ncc)
            {
                ncc.CollectionChanged -= OnItemsSourceCollectionChanged;
            }
            UnsubscribeFromItemProperties(source);
        }

        private void SubscribeToItemProperties(IEnumerable? source)
        {
            if (source == null) return;
            foreach (var item in source)
            {
                if (item is INotifyPropertyChanged npc)
                {
                    npc.PropertyChanged += OnItemPropertyChanged;
                }
            }
        }

        private void UnsubscribeFromItemProperties(IEnumerable? source)
        {
            if (source == null) return;
            foreach (var item in source)
            {
                if (item is INotifyPropertyChanged npc)
                {
                    npc.PropertyChanged -= OnItemPropertyChanged;
                }
            }
        }

        private void OnItemsSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                UnsubscribeFromItemProperties(e.OldItems);
                foreach (var item in e.OldItems)
                {
                    _selectedItems.Remove(item);
                }
            }
            if (e.NewItems != null)
            {
                SubscribeToItemProperties(e.NewItems);
                foreach (var item in e.NewItems)
                {
                    if (item == null) continue;
                    var prop = item.GetType().GetProperty("IsSelected");
                    if (prop != null && prop.PropertyType == typeof(bool))
                    {
                        try
                        {
                            var val = (bool)prop.GetValue(item)!;
                            if (val && !_selectedItems.Contains(item))
                            {
                                _selectedItems.Add(item);
                            }
                        }
                        catch { }
                    }
                }
            }
            UpdateVisualStates();
        }

        private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "IsSelected" && sender != null)
            {
                var prop = sender.GetType().GetProperty("IsSelected");
                if (prop != null)
                {
                    try
                    {
                        bool val = (bool)prop.GetValue(sender)!;
                        if (val)
                        {
                            if (!_selectedItems.Contains(sender))
                            {
                                _selectedItems.Add(sender);
                                UpdateVisualStates();
                                RaiseSelectionChanged(new[] { sender }, Array.Empty<object>());
                            }
                        }
                        else
                        {
                            if (_selectedItems.Contains(sender))
                            {
                                _selectedItems.Remove(sender);
                                UpdateVisualStates();
                                RaiseSelectionChanged(Array.Empty<object>(), new[] { sender });
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        private void OnElementPrepared(object? sender, ItemsRepeaterElementPreparedEventArgs e)
        {
            if (e.Element is Control control)
            {
                var item = e.Element.DataContext;
                if (item != null)
                {
                    bool isSelected = _selectedItems.Contains(item);
                    bool isFocused = _focusedIndex == e.Index;
                    control.Classes.Set("selected", isSelected);
                    control.Classes.Set("focused", isFocused);
                }
            }
        }

        private void OnElementClearing(object? sender, ItemsRepeaterElementClearingEventArgs e)
        {
            if (e.Element is Control control)
            {
                control.Classes.Remove("selected");
                control.Classes.Remove("focused");
            }
        }

        private async void OnScrollViewerScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (ItemsSource is ISupportIncrementalLoading incrementalSource)
            {
                var extentHeight = PartScrollViewer.Extent.Height;
                var viewportHeight = PartScrollViewer.Viewport.Height;
                var offset = PartScrollViewer.Offset.Y;

                if (extentHeight > 0 && offset + viewportHeight >= extentHeight - 150)
                {
                    if (incrementalSource.HasMoreItems && !_isIncrementalLoading)
                    {
                        _isIncrementalLoading = true;
                        try
                        {
                            await incrementalSource.LoadMoreItemsAsync(100);
                        }
                        finally
                        {
                            _isIncrementalLoading = false;
                        }
                    }
                }
            }
        }

        private void OnItemsRepeaterPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var point = e.GetCurrentPoint(this);
            bool isLeft = point.Properties.IsLeftButtonPressed;
            bool isRight = point.Properties.IsRightButtonPressed;

            if (!isLeft && !isRight)
                return;

            // Stop selection handling if user clicked a button or interactive child element
            if (e.Source is Control source && source.GetSelfAndVisualAncestors().OfType<Button>().Any())
                return;

            var visual = e.Source as Visual;
            Control? container = null;
            while (visual != null && visual != PartItemsRepeater)
            {
                if (visual is Control ctrl && PartItemsRepeater.Children.Contains(ctrl))
                {
                    container = ctrl;
                    break;
                }
                visual = visual.GetVisualParent();
            }

            if (container != null && container.DataContext is object item)
            {
                int index = PartItemsRepeater.GetElementIndex(container);
                if (index >= 0)
                {
                    if (isRight)
                    {
                        if (!_selectedItems.Contains(item))
                        {
                            ClearSelectionInternal();
                            SelectIndexInternal(index, item);
                            _selectionAnchorIndex = index;
                            _focusedIndex = index;
                            UpdateVisualStates();
                        }
                        // Do NOT mark Handled=true for right clicks so context menus can trigger!
                        return;
                    }

                    ApplyPointerSelection(index, item, e.KeyModifiers);
                    Focus();
                    e.Handled = true;
                }
            }
        }

        private void ApplyPointerSelection(int index, object item, KeyModifiers modifiers)
        {

            if (SelectionMode == SelectionMode.Single)
            {
                ClearSelectionInternal();
                SelectIndexInternal(index, item);
                _selectionAnchorIndex = index;
                _focusedIndex = index;
            }
            else if (SelectionMode == SelectionMode.Multiple || SelectionMode == SelectionMode.Toggle)
            {
                if ((modifiers & KeyModifiers.Shift) == KeyModifiers.Shift && _selectionAnchorIndex >= 0)
                {
                    ClearSelectionInternal();
                    int start = Math.Min(_selectionAnchorIndex, index);
                    int end = Math.Max(_selectionAnchorIndex, index);
                    for (int i = start; i <= end; i++)
                    {
                        var targetItem = GetItemAt(i);
                        if (targetItem != null)
                        {
                            SelectIndexInternal(i, targetItem);
                        }
                    }
                    _focusedIndex = index;
                }
                else if ((modifiers & KeyModifiers.Control) == KeyModifiers.Control || SelectionMode == SelectionMode.Toggle)
                {
                    if (_selectedItems.Contains(item))
                    {
                        DeselectIndexInternal(index, item);
                    }
                    else
                    {
                        SelectIndexInternal(index, item);
                    }
                    _selectionAnchorIndex = index;
                    _focusedIndex = index;
                }
                else
                {
                    ClearSelectionInternal();
                    SelectIndexInternal(index, item);
                    _selectionAnchorIndex = index;
                    _focusedIndex = index;
                }
            }
            UpdateVisualStates();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            int count = GetItemsCount();
            if (count == 0 || PartItemsRepeater == null) return;

            int currentIndex = _focusedIndex >= 0 ? _focusedIndex : 0;
            int targetIndex = currentIndex;

            switch (e.Key)
            {
                case Key.Up:
                    targetIndex = currentIndex - 1;
                    break;
                case Key.Down:
                    targetIndex = currentIndex + 1;
                    break;
                case Key.PageUp:
                    targetIndex = currentIndex - 10;
                    break;
                case Key.PageDown:
                    targetIndex = currentIndex + 10;
                    break;
                case Key.Home:
                    targetIndex = 0;
                    break;
                case Key.End:
                    targetIndex = count - 1;
                    break;
                case Key.Space:
                    var focusedItem = GetItemAt(currentIndex);
                    if (focusedItem != null)
                    {
                        if (_selectedItems.Contains(focusedItem))
                            DeselectIndexInternal(currentIndex, focusedItem);
                        else
                            SelectIndexInternal(currentIndex, focusedItem);
                        UpdateVisualStates();
                    }
                    e.Handled = true;
                    return;
                default:
                    return;
            }

            targetIndex = Math.Clamp(targetIndex, 0, count - 1);
            if (targetIndex != currentIndex || _focusedIndex == -1)
            {
                _focusedIndex = targetIndex;
                var targetItem = GetItemAt(targetIndex);
                if (targetItem != null)
                {
                    if (SelectionMode == SelectionMode.Single)
                    {
                        ClearSelectionInternal();
                        SelectIndexInternal(targetIndex, targetItem);
                        _selectionAnchorIndex = targetIndex;
                    }
                    else if (SelectionMode == SelectionMode.Multiple)
                    {
                        if ((e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift && _selectionAnchorIndex >= 0)
                        {
                            ClearSelectionInternal();
                            int start = Math.Min(_selectionAnchorIndex, targetIndex);
                            int end = Math.Max(_selectionAnchorIndex, targetIndex);
                            for (int i = start; i <= end; i++)
                            {
                                var item = GetItemAt(i);
                                if (item != null) SelectIndexInternal(i, item);
                            }
                        }
                        else if ((e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control)
                        {
                            // Shift/Ctrl selection rules: Ctrl just moves focus/anchor
                            _selectionAnchorIndex = targetIndex;
                        }
                        else
                        {
                            ClearSelectionInternal();
                            SelectIndexInternal(targetIndex, targetItem);
                            _selectionAnchorIndex = targetIndex;
                        }
                    }
                }

                var element = PartItemsRepeater.GetOrCreateElement(targetIndex);
                if (element is Control ctrl)
                {
                    ctrl.BringIntoView();
                }
                UpdateVisualStates();
                e.Handled = true;
            }
        }

        private void SelectIndexInternal(int index, object item)
        {
            if (!_selectedItems.Contains(item))
            {
                _selectedItems.Add(item);
                SetIsSelectedOnItem(item, true);
                RaiseSelectionChanged(new[] { item }, Array.Empty<object>());
            }
        }

        private void DeselectIndexInternal(int index, object item)
        {
            if (_selectedItems.Contains(item))
            {
                _selectedItems.Remove(item);
                SetIsSelectedOnItem(item, false);
                RaiseSelectionChanged(Array.Empty<object>(), new[] { item });
            }
        }

        private void ClearSelectionInternal()
        {
            var removed = _selectedItems.ToArray();
            if (removed.Length > 0)
            {
                _selectedItems.Clear();
                foreach (var item in removed)
                {
                    SetIsSelectedOnItem(item, false);
                }
                RaiseSelectionChanged(Array.Empty<object>(), removed);
            }
        }

        private void SetIsSelectedOnItem(object item, bool value)
        {
            if (_isUpdatingSelection) return;
            _isUpdatingSelection = true;
            try
            {
                var prop = item.GetType().GetProperty("IsSelected");
                if (prop != null && prop.CanWrite && prop.PropertyType == typeof(bool))
                {
                    prop.SetValue(item, value);
                }
            }
            catch { }
            finally
            {
                _isUpdatingSelection = false;
            }
        }

        private void RaiseSelectionChanged(IList added, IList removed)
        {
            if (added.Count == 0 && removed.Count == 0) return;
            RaiseEvent(new SelectionChangedEventArgs(SelectionChangedEvent, removed, added));
        }

        private int GetItemsCount()
        {
            if (ItemsSource is ICollection collection)
                return collection.Count;
            if (ItemsSource is IList list)
                return list.Count;

            int count = 0;
            if (ItemsSource != null)
            {
                var enumerator = ItemsSource.GetEnumerator();
                while (enumerator.MoveNext()) count++;
            }
            return count;
        }

        private object? GetItemAt(int index)
        {
            if (ItemsSource is IList list)
            {
                if (index >= 0 && index < list.Count)
                    return list[index];
            }
            else if (ItemsSource != null)
            {
                int count = 0;
                foreach (var item in ItemsSource)
                {
                    if (count == index) return item;
                    count++;
                }
            }
            return null;
        }

        private void UpdateVisualStates()
        {
            if (PartItemsRepeater == null) return;

            int count = GetItemsCount();
            for (int i = 0; i < count; i++)
            {
                var element = PartItemsRepeater.TryGetElement(i);
                if (element is Control control)
                {
                    var item = GetItemAt(i);
                    if (item != null)
                    {
                        bool isSelected = _selectedItems.Contains(item);
                        bool isFocused = _focusedIndex == i;
                        control.Classes.Set("selected", isSelected);
                        control.Classes.Set("focused", isFocused);
                    }
                }
            }
        }

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);
            // Wire pointer pressed on ItemsRepeater to detect rows
            PartItemsRepeater.PointerPressed += OnItemsRepeaterPointerPressed;
        }
    }
}
