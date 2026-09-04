using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Extensions;
using MudBlazor.Services;
using MudBlazor.State;
using MudBlazor.Utilities;

namespace MudBlazor
{
    /// <summary>
    /// An extensively customizable tree view component for displaying hierarchical data, featuring item selection, lazy-loading, and templating support.
    /// </summary>
    /// <typeparam name="T">The type of item to display.</typeparam>
    /// <seealso cref="MudTreeViewItem{T}"/>
    /// <seealso cref="MudTreeViewItemToggleButton"/>
    public partial class MudTreeView<T> : MudComponentBase, IAsyncDisposable
    {
        private const float DefaultItemSize = 40f;
        private const float DefaultDenseItemSize = 26f;

        public MudTreeView()
        {
            MudTreeRoot = this;
            using var registerScope = CreateRegisterScope();
            _selectedValueState = registerScope.RegisterParameter<T?>(nameof(SelectedValue))
                .WithParameter(() => SelectedValue)
                .WithEventCallback(() => SelectedValueChanged)
                .WithChangeHandler(OnSelectedValueChangedAsync)
                .WithComparer(() => Comparer);
            _selectedValuesState = registerScope.RegisterParameter<IReadOnlyCollection<T>?>(nameof(SelectedValues))
                .WithParameter(() => SelectedValues)
                .WithEventCallback(() => SelectedValuesChanged)
                .WithChangeHandler(OnSelectedValuesChangedAsync)
                .WithComparer(() => Comparer, comparer => new CollectionComparer<T>(comparer));
            registerScope.RegisterParameter<IEqualityComparer<T?>>(nameof(Comparer))
                .WithParameter(() => Comparer)
                .WithChangeHandler(OnComparerChangedAsync);
            registerScope.RegisterParameter<SelectionMode>(nameof(SelectionMode))
                .WithParameter(() => SelectionMode)
                .WithChangeHandler(OnParameterChangedAsync);
            registerScope.RegisterParameter<bool>(nameof(TriState))
                .WithParameter(() => TriState)
                .WithChangeHandler(OnParameterChangedAsync);
            registerScope.RegisterParameter<bool>(nameof(Disabled))
                .WithParameter(() => Disabled)
                .WithChangeHandler(OnParameterChangedAsync);
            registerScope.RegisterParameter<bool>(nameof(ReadOnly))
                .WithParameter(() => ReadOnly)
                .WithChangeHandler(OnParameterChangedAsync);
            _selection = new();
        }

        private readonly ParameterState<T?> _selectedValueState;
        private readonly ParameterState<IReadOnlyCollection<T>?> _selectedValuesState;

        private HashSet<T> _selection;
        private readonly HashSet<MudTreeViewItem<T>> _childItems = new();
        private readonly string _treeElementId = Identifier.Create("mud-treeview");
        private readonly TreeViewProjection<T> _projection = new();
        // ServerData load state belongs to the backing node object, not the rendered component instance.
        // When the parent replaces Items with new node objects, the old entries can disappear with them.
        private readonly TreeViewServerLoadState<T> _serverLoadState = new();
        private ElementReference _treeElement;
        private string? _subscribedElementId;
        private int _keyInterceptorUpdateVersion;
        private bool _isFirstRender = true;
        private bool _isDisposed;
        private bool _hasLoggedInvalidVirtualizeConfiguration;
        private bool _projectionDirty = true;
        private bool _reconcileSelection;
        private bool _spacersMarked;
        private bool _wasVirtualized;
        private IReadOnlyCollection<ITreeItemData<T>>? _lastVirtualizedItems;
        // The row which owns aria-activedescendant. It is tracked by backing item so that it survives scrolling and re-rendering.
        private ITreeItemData<T>? _activeItem;
        private TreeViewItemContext<T>? _activeRow;
        // Whether the active row was chosen by the tree rather than by the user, so it may still follow the selection.
        private bool _activeItemIsDefault;
        private bool _scrollToActiveItem;
        private (ITreeItemData<T> Item, bool SelectOnly)? _pendingActivation;
        internal bool MultiSelection => SelectionMode == SelectionMode.MultiSelection;
        private bool ToggleSelection => SelectionMode == SelectionMode.ToggleSelection;

        protected string Classname =>
            new CssBuilder("mud-treeview")
                .AddClass("mud-treeview-dense", Dense)
                .AddClass("mud-treeview-hover", !Disabled && Hover && (!ReadOnly || ExpandOnClick))
                .AddClass("mud-treeview-virtualized", IsVirtualized)
                .AddClass($"mud-treeview-selected-{Color.ToStringFast(true)}")
                .AddClass($"mud-treeview-checked-{CheckBoxColor.ToStringFast(true)}")
                .AddClass(Class)
                .Build();

        protected string Stylename =>
            new StyleBuilder()
                .AddStyle($"width", Width, !string.IsNullOrWhiteSpace(Width))
                .AddStyle($"height", Height, !string.IsNullOrWhiteSpace(Height))
                .AddStyle($"max-height", MaxHeight, !string.IsNullOrWhiteSpace(MaxHeight))
                .AddStyle(Style)
                .Build();

        [CascadingParameter]
        private MudTreeView<T> MudTreeRoot { get; set; }

        [Inject]
        private IKeyInterceptorService KeyInterceptorService { get; set; } = null!;

        [Inject]
        private IScrollManager ScrollManager { get; set; } = null!;

        [Inject]
        private IJSRuntime JSRuntime { get; set; } = null!;

        /// <summary>
        /// The color of the selected item.
        /// </summary>
        /// <remarks>
        /// Defaults to <see cref="Color.Primary"/>.
        /// </remarks>
        [Parameter]
        [Category(CategoryTypes.TreeView.Selecting)]
        public Color Color { get; set; } = Color.Primary;

        /// <summary>
        /// The color of checkboxes.
        /// </summary>
        /// <remarks>
        /// Defaults to <see cref="Color.Default"/>. Only applies when <see cref="SelectionMode"/> is <see cref="SelectionMode.MultiSelection" />.
        /// </remarks>
        [Parameter]
        [Category(CategoryTypes.TreeView.Selecting)]
        public Color CheckBoxColor { get; set; }

        /// <summary>
        /// Controls how many items can be selected at one time.
        /// </summary>
        /// <remarks>
        /// Defaults to <see cref="SelectionMode.SingleSelection"/>.
        /// </remarks>
        [Parameter]
        [Category(CategoryTypes.TreeView.Selecting)]
        public SelectionMode SelectionMode { get; set; } = SelectionMode.SingleSelection;

        /// <summary>
        /// Uses checkboxes which support an undetermined state.
        /// </summary>
        /// <remarks>
        /// Defaults to <c>true</c>. Only applies when <see cref="SelectionMode"/> is <see cref="SelectionMode.MultiSelection"/>. When set,
        /// an item's checkbox will be in the "undetermined" state if child items have a mix of checked and unchecked states.
        /// </remarks>
        [Parameter]
        [Category(CategoryTypes.TreeView.Selecting)]
        public bool TriState { get; set; } = true;

        /// <summary>
        /// Automatically checks an item if all children are selected.
        /// </summary>
        /// <remarks>
        /// Defaults to <c>true</c>. Only applies when <see cref="SelectionMode"/> is <see cref="SelectionMode.MultiSelection"/>.
        /// Items will also be deselected if any children are deselected.
        /// </remarks>
        [Parameter]
        [Category(CategoryTypes.TreeView.Selecting)]
        public bool AutoSelectParent { get; set; } = true;

        /// <summary>
        /// Expands an item with children if it is clicked anywhere (not just the expand/collapse buttons).
        /// </summary>
        /// <remarks>
        /// Defaults to <c>false</c>.
        /// </remarks>
        [Parameter]
        [Category(CategoryTypes.TreeView.ClickAction)]
        public bool ExpandOnClick { get; set; }

        /// <summary>
        /// Expands an item with children if it is double-clicked anywhere (not just the expand/collapse buttons).
        /// </summary>
        /// <remarks>
        /// Defaults to <c>false</c>.
        /// </remarks>
        [Parameter]
        [Category(CategoryTypes.TreeView.ClickAction)]
        public bool ExpandOnDoubleClick { get; set; }

        /// <summary>
        /// Automatically expands items to show selected children.
        /// </summary>
        /// <remarks>
        /// Defaults to <c>false</c>.
        /// </remarks>
        [Parameter]
        [Category(CategoryTypes.TreeView.Selecting)]
        public bool AutoExpand { get; set; }

        /// <summary>
        /// Shows an effect when items are hovered over.
        /// </summary>
        /// <remarks>
        /// Defaults to <c>false</c>.
        /// </remarks>
        [Parameter]
        [Category(CategoryTypes.TreeView.Appearance)]
        public bool Hover { get; set; }

        /// <summary>
        /// Uses compact vertical padding.
        /// </summary>
        /// <remarks>
        /// Defaults to <c>false</c>.
        /// </remarks>
        [Parameter]
        [Category(CategoryTypes.TreeView.Appearance)]
        public bool Dense { get; set; }

        /// <summary>
        /// Renders only visible data items instead of all items.
        /// </summary>
        /// <remarks>
        /// Defaults to <c>false</c>. Only works when <see cref="Height"/> or <see cref="MaxHeight"/> is set, and only applies when <see cref="Items"/> and <see cref="ItemTemplate"/> are set.
        /// The virtualized tree reads <see cref="ITreeItemData{T}.Expanded"/>, <see cref="ITreeItemData{T}.Children"/>, <see cref="ITreeItemData{T}.Selected"/>, and <see cref="ITreeItemData{T}.Visible"/>
        /// from the backing data, so bind the item template to those properties. Each backing item should be a distinct instance.
        /// </remarks>
        [Parameter]
        [Category(CategoryTypes.TreeView.Behavior)]
        public bool Virtualize { get; set; }

        /// <summary>
        /// The number of additional items rendered outside the visible region when <see cref="Virtualize"/> is <c>true</c>.
        /// </summary>
        /// <remarks>
        /// Defaults to <c>3</c>. This value can reduce the amount of rendering during scrolling, but higher values can affect performance.
        /// </remarks>
        [Parameter]
        [Category(CategoryTypes.TreeView.Behavior)]
        public int OverscanCount { get; set; } = 3;

        /// <summary>
        /// The height of each item, in pixels, when <see cref="Virtualize"/> is <c>true</c>.
        /// </summary>
        /// <remarks>
        /// Defaults to <c>0</c>, which uses <c>40</c> normally and <c>26</c> when <see cref="Dense"/> is <c>true</c>.
        /// </remarks>
        [Parameter]
        [Category(CategoryTypes.TreeView.Behavior)]
        public float ItemSize { get; set; }

        /// <summary>
        /// The maximum number of items rendered when <see cref="Virtualize"/> is <c>true</c>.
        /// </summary>
        /// <remarks>
        /// Defaults to <see cref="int.MaxValue"/>. This only affects .NET 9 and later.
        /// </remarks>
        [Parameter]
        [Category(CategoryTypes.TreeView.Behavior)]
        public int MaxItemCount { get; set; } = int.MaxValue;

        /// <summary>
        /// Sets a fixed height.
        /// </summary>
        /// <remarks>
        /// Defaults to <c>null</c>. Can be a CSS value such as <c>500px</c> or <c>30%</c>. When set, items can be scrolled vertically. Otherwise, the height will grow automatically.
        /// </remarks>
        [Parameter]
        [Category(CategoryTypes.TreeView.Appearance)]
        public string? Height { get; set; }

        /// <summary>
        /// Sets a maximum height.
        /// </summary>
        /// <remarks>
        /// Defaults to <c>null</c>. Can be a CSS value such as <c>500px</c> or <c>30%</c>. When set, items can be scrolled vertically. Otherwise, the height will grow automatically.
        /// </remarks>
        [Parameter]
        [Category(CategoryTypes.TreeView.Appearance)]
        public string? MaxHeight { get; set; }

        /// <summary>
        /// Sets a fixed width.
        /// </summary>
        /// <remarks>
        /// Defaults to <c>null</c>. Can be a CSS value such as <c>500px</c> or <c>30%</c>.
        /// </remarks>
        [Parameter]
        [Category(CategoryTypes.TreeView.Appearance)]
        public string? Width { get; set; }

        /// <summary>
        /// Prevents the user from interacting with any items.
        /// </summary>
        /// <remarks>
        /// Defaults to <c>false</c>.
        /// </remarks>
        [Parameter]
        [Category(CategoryTypes.TreeView.Behavior)]
        public bool Disabled { get; set; }

        /// <summary>
        /// Determines whether items are displayed.
        /// </summary>
        /// <remarks>
        /// Defaults to <c>null</c>. The function provides an item and should return <c>true</c> to display the item, or <c>false</c> to hide it.
        /// </remarks>
        [Parameter]
        [Category(CategoryTypes.TreeView.Behavior)]
        public Func<ITreeItemData<T>, Task<bool>>? FilterFunc { get; set; }

        /// <summary>
        /// Shows a ripple effect when an item is clicked.
        /// </summary>
        /// <remarks>
        /// Defaults to <c>true</c>.
        /// </remarks>
        [Parameter]
        [Category(CategoryTypes.TreeView.Appearance)]
        public bool Ripple { get; set; } = true;

        /// <summary>
        /// The items being displayed.
        /// </summary>
        [Parameter]
        [Category(CategoryTypes.TreeView.Data)]
        public IReadOnlyCollection<ITreeItemData<T>>? Items { get; set; } = Array.Empty<ITreeItemData<T>>();

        /// <summary>
        /// The currently selected value.
        /// </summary>
        /// <remarks>
        /// Applies when <see cref="SelectionMode"/> is <see cref="SelectionMode.SingleSelection"/>.
        /// </remarks>
        [Parameter, ParameterState]
        [Category(CategoryTypes.TreeView.Selecting)]
        public T? SelectedValue { get; set; }

        /// <summary>
        /// Occurs when <see cref="SelectedValue"/> has changed.
        /// </summary>
        [Parameter]
        public EventCallback<T?> SelectedValueChanged { get; set; }

        /// <summary>
        /// The currently selected values.
        /// </summary>
        /// <remarks>
        /// Applies when <see cref="SelectionMode"/> is <see cref="SelectionMode.MultiSelection"/> or <see cref="SelectionMode.ToggleSelection"/>.
        /// </remarks>
        [Parameter, ParameterState]
        [Category(CategoryTypes.TreeView.Selecting)]
        public IReadOnlyCollection<T>? SelectedValues { get; set; }

        /// <summary>
        /// Occurs when <see cref="SelectedValues"/> has changed.
        /// </summary>
        [Parameter]
        public EventCallback<IReadOnlyCollection<T>?> SelectedValuesChanged { get; set; }

        /// <summary>
        /// The content within this component.
        /// </summary>
        /// <remarks>
        /// Applies when <see cref="ItemTemplate"/> and <see cref="Items"/> are both not set.
        /// </remarks>
        [Parameter]
        [Category(CategoryTypes.TreeView.Data)]
        public RenderFragment? ChildContent { get; set; }

        /// <summary>
        /// The template for rendering each item.
        /// </summary>
        [Parameter]
        [Category(CategoryTypes.TreeView.Data)]
        public RenderFragment<ITreeItemData<T>>? ItemTemplate { get; set; }

        /// <summary>
        /// The comparer used to check if two items are equal.
        /// </summary>
        [Parameter]
        [Category(CategoryTypes.TreeView.Selecting)]
        public IEqualityComparer<T?> Comparer { get; set; } = EqualityComparer<T?>.Default;

        /// <summary>
        /// The function for asynchronously loading items.
        /// </summary>
        /// <remarks>
        /// When set, the function will be called to load the children of a parent item.
        /// When the parent node is <c>null</c>, top-level items should be returned.
        /// </remarks>
        [Parameter]
        [Category(CategoryTypes.TreeView.Data)]
        public Func<T?, Task<IReadOnlyCollection<TreeItemData<T>>>>? ServerData { get; set; }

        /// <summary>
        /// Prevents selections from being changed.
        /// </summary>
        /// <remarks>
        /// Defaults to <c>false</c>. When <c>true</c>, selections cannot be changed, but the current
        /// selections will continue to be displayed.
        /// </remarks>
        [Parameter]
        [Category(CategoryTypes.List.Selecting)]
        public bool ReadOnly { get; set; }

        /// <summary>
        /// The icon displayed for checked items.
        /// </summary>
        /// <remarks>
        /// Defaults to <see cref="Icons.Material.Filled.CheckBox"/>.
        /// </remarks>
        [Parameter]
        [Category(CategoryTypes.TreeView.Selecting)]
        public string CheckedIcon { get; set; } = Icons.Material.Filled.CheckBox;

        /// <summary>
        /// The icon displayed for unchecked items.
        /// </summary>
        /// <remarks>
        /// Defaults to <see cref="Icons.Material.Filled.CheckBoxOutlineBlank"/>.
        /// </remarks>
        [Parameter]
        [Category(CategoryTypes.TreeView.Selecting)]
        public string UncheckedIcon { get; set; } = Icons.Material.Filled.CheckBoxOutlineBlank;

        /// <summary>
        /// The icon displayed for indeterminate items.
        /// </summary>
        /// <remarks>
        /// Defaults to <see cref="Icons.Material.Filled.IndeterminateCheckBox"/>. Only applies when <see cref="TriState"/> is <c>true</c>.
        /// </remarks>
        [Parameter]
        [Category(CategoryTypes.TreeView.Selecting)]
        public string IndeterminateIcon { get; set; } = Icons.Material.Filled.IndeterminateCheckBox;

        /// <inheritdoc />
        protected override void OnParametersSet()
        {
            base.OnParametersSet();

            if (MudTreeRoot == this)
            {
                var isVirtualized = HasValidVirtualizationConfiguration;
                _projectionDirty = true;
                if (isVirtualized)
                {
                    _reconcileSelection = true;
                    if (!_wasVirtualized || !ReferenceEquals(_lastVirtualizedItems, Items))
                    {
                        _activeItem = null;
                        _activeRow = null;
                        _activeItemIsDefault = false;
                    }
                }
                else
                {
                    _pendingActivation = null;
                    _scrollToActiveItem = false;
                }

                _wasVirtualized = isVirtualized;
                _lastVirtualizedItems = Items;
            }

            if (Virtualize && !HasValidVirtualizationConfiguration && !_hasLoggedInvalidVirtualizeConfiguration)
            {
                Logger.LogWarning(
                    "{Component} requires {Items}, {ItemTemplate}, and either {Height} or {MaxHeight} when {Virtualize} is true. Falling back to standard rendering.",
                    nameof(MudTreeView<T>),
                    nameof(Items),
                    nameof(ItemTemplate),
                    nameof(Height),
                    nameof(MaxHeight),
                    nameof(Virtualize));
                _hasLoggedInvalidVirtualizeConfiguration = true;
            }
        }

        /// <inheritdoc />
        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (MudTreeRoot == this)
            {
                if (firstRender)
                {
                    _isFirstRender = false;
                }

                var reconcileSelection = _reconcileSelection;
                _reconcileSelection = false;
                var shouldRefresh = false;
                var selectionChanged = false;
                if (reconcileSelection && IsVirtualized)
                {
                    var result = await ReconcileVirtualizedSelectionAsync();
                    selectionChanged = result.SelectionChanged;
                    if (result.Changed)
                    {
                        await UpdateItemsAsync();
                        shouldRefresh = true;
                    }
                }

                // Auto-expansion follows selection transitions only, so that a branch the user collapsed stays collapsed
                // when the tree merely re-renders.
                if (firstRender || selectionChanged)
                {
                    shouldRefresh = ApplyVirtualizedAutoExpand(GetSelection()) || shouldRefresh;
                }

                if (firstRender && !IsVirtualized)
                {
                    await UpdateItemsAsync();
                }

                if (shouldRefresh)
                {
                    RefreshProjection();
                }

                await UpdateKeyInterceptorAsync();

                if (IsVirtualized)
                {
                    if (!_spacersMarked)
                    {
                        _spacersMarked = true;
                        await JSRuntime.InvokeVoidAsyncIgnoreErrors(
                            "mudElementRef.setChildrenAttributes",
                            _treeElement,
                            ":scope > li:not(.mud-treeview-item)",
                            new Dictionary<string, string> { ["role"] = "presentation", ["aria-hidden"] = "true" });
                    }

                    if (_scrollToActiveItem)
                    {
                        await ScrollActiveItemIntoViewAsync();
                    }
                }
                else
                {
                    _spacersMarked = false;
                }
            }

            await base.OnAfterRenderAsync(firstRender);
        }

        /// <summary>
        /// Filters all items based on the <see cref="FilterFunc"/> function.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task FilterAsync()
        {
            if (Items is null)
            {
                return;
            }

            if (IsVirtualized)
            {
                var changed = FilterFunc is null
                    ? TreeViewHierarchy<T>.ResetFilter(Items)
                    : await TreeViewHierarchy<T>.FilterAsync(Items, FilterFunc);
                if (changed)
                {
                    RefreshProjection();
                }
                return;
            }

            if (FilterFunc is null)
            {
                ResetFilter(Items);
                return;
            }

            await TraverseFilterAsync(Items);
        }

        /// <summary>
        /// The internal filter logic that traverses the tree recursively and applies the <see cref="FilterFunc"/> to every item to set the <see cref="MudTreeViewItem{T}.Visible"/> property
        /// </summary>
        /// <param name="items">The hierarchical tree structure to traverse</param>
        /// <returns>A task to represent the asynchronous operation.</returns>
        private async Task TraverseFilterAsync(IEnumerable<ITreeItemData<T>> items)
        {
            foreach (var item in items)
            {
                if (item.HasChildren)
                {
                    /* Recursively traverse the tree. Since HasChildren performs the null check on the children we can use the null-forgiving operator here.
                     * Same goes for the FilterFunc which is checked for null in the public Filter function that invokes this function.
                     */
                    await TraverseFilterAsync(item.Children);
                    item.Expanded = item.Visible = await FilterFunc!(item) || item.Children.Any(c => c.Visible);
                }
                else
                {
                    item.Expanded = item.Visible = await FilterFunc!(item);
                }
            }
        }

        /// <summary>
        /// Resets the filter, so that all <see cref="MudTreeViewItem{T}.Visible"/> are set to true and the entire tree is visible.
        /// </summary>
        /// <param name="items">The items to reset</param>
        private static void ResetFilter(IEnumerable<ITreeItemData<T>> items)
        {
            foreach (var item in items)
            {
                if (item.HasChildren)
                {
                    ResetFilter(item.Children);
                }

                item.Visible = true;
            }
        }

        /// <summary>
        /// Expands all items and their children.
        /// </summary>
        public async Task ExpandAllAsync()
        {
            if (IsVirtualized)
            {
                if (TreeViewHierarchy<T>.ExpandAll(Items))
                {
                    RefreshProjection();
                }
                return;
            }

            foreach (var item in _childItems)
            {
                await item.ExpandAllAsync();
            }
        }

        /// <summary>
        /// Collapses all items and their children.
        /// </summary>
        public async Task CollapseAllAsync()
        {
            if (IsVirtualized)
            {
                if (TreeViewHierarchy<T>.CollapseAll(Items))
                {
                    RefreshProjection();
                }
                return;
            }

            foreach (var item in _childItems)
            {
                await item.CollapseAllAsync();
            }
        }

        /// <summary>
        /// SingleSelection or ToggleSelection: SelectedValue was updated via binding
        /// </summary>
        private Task OnSelectedValueChangedAsync(ParameterChangedEventArgs<T?> args)
        {
            // on first render the children are not yet initialized, so ignore this update
            if (_isFirstRender)
            {
                return Task.CompletedTask;
            }
            return SetSelectedValueAsync(args.Value);
        }

        /// <summary>
        /// MultiSelection: SelectedValues was updated via binding
        /// </summary>
        private Task OnSelectedValuesChangedAsync(ParameterChangedEventArgs<IReadOnlyCollection<T>?> args)
        {
            if (_isFirstRender)
            {
                // on first render the children are not yet initialized, so just initialize the selection
                _selection = args.Value is not null ? new HashSet<T>(args.Value!, Comparer) : new HashSet<T>(Comparer);
                return Task.CompletedTask;
            }
            return SetSelectedValuesAsync(args.Value ?? Array.Empty<T>());
        }

        private Task OnComparerChangedAsync(ParameterChangedEventArgs<IEqualityComparer<T?>> args)
        {
            if (_isFirstRender)
            {
                return Task.CompletedTask;
            }
            return UpdateItemsAsync();
        }

        private Task OnParameterChangedAsync()
        {
            if (_isFirstRender)
            {
                return Task.CompletedTask;
            }
            return UpdateItemsAsync();
        }

        #region Virtualization

        [MemberNotNullWhen(true, nameof(ItemTemplate), nameof(Items))]
        private bool HasValidVirtualizationConfiguration =>
            Virtualize
            && ItemTemplate is not null
            && Items is not null
            && (!string.IsNullOrWhiteSpace(Height) || !string.IsNullOrWhiteSpace(MaxHeight));

        [MemberNotNullWhen(true, nameof(ItemTemplate), nameof(Items))]
        internal bool IsVirtualized => HasValidVirtualizationConfiguration;

        internal bool IsDisposed => _isDisposed;

        /// <summary>
        /// The flattened visible rows, rebuilt at most once per render after the backing data changed.
        /// </summary>
        private ReadOnlyCollection<TreeViewItemContext<T>> Rows
        {
            get
            {
                if (_projectionDirty)
                {
                    RebuildProjection();
                }

                return _projection.Rows;
            }
        }

        /// <summary>
        /// The row which currently owns <c>aria-activedescendant</c>, or <c>null</c> when the active item is not visible.
        /// </summary>
        private TreeViewItemContext<T>? ActiveRow
        {
            get
            {
                _ = Rows;
                return _activeRow;
            }
        }

        private void RebuildProjection()
        {
            _projection.Rebuild(Items, GetSelection(), Comparer);
            _projectionDirty = false;
            ReconcileActiveItem();
        }

        /// <summary>
        /// Marks the projection stale and re-renders the tree.
        /// </summary>
        /// <param name="reconcileSelection">Whether backing <see cref="ITreeItemData{T}.Selected"/> changes should be reconciled after the render.</param>
        /// <param name="alwaysRender">Whether the tree re-renders even when it is not virtualized.</param>
        internal void RefreshProjection(bool reconcileSelection = false, bool alwaysRender = false)
        {
            if (_isDisposed)
            {
                return;
            }

            _projectionDirty = true;
            // Backing Selected flags are only a source of truth for the virtualized renderer; a standard tree keeps
            // its selection in the item components and must not have it rewritten from the data.
            _reconcileSelection |= reconcileSelection && IsVirtualized;
            if (IsVirtualized || alwaysRender)
            {
                StateHasChanged();
            }
        }

        /// <summary>
        /// Keeps the active item pointing at a visible row after the projection changed.
        /// </summary>
        /// <remarks>
        /// An item hidden in place (collapsed or filtered) hands the active row to its nearest visible ancestor;
        /// an item which was removed or moved hands it to the row at its previous position.
        /// A tree without an active item activates its first selected row, or its first row.
        /// </remarks>
        private void ReconcileActiveItem()
        {
            var rows = _projection.Rows;
            if (_activeItem is not null)
            {
                var row = _projection.Find(_activeItem);
                if (row is null)
                {
                    row = FindRecoveryRow(_activeRow);
                    _pendingActivation = null;
                }

                _activeItem = row?.Item;
                _activeRow = row;
                if (row is null)
                {
                    _scrollToActiveItem = false;
                }
            }

            if (rows.Count > 0 && (_activeItem is null || (_activeItemIsDefault && _activeRow?.IsSelected != true)))
            {
                var selectedRow = rows.FirstOrDefault(row => row.IsSelected);
                var defaultRow = selectedRow ?? rows[0];
                if (!ReferenceEquals(defaultRow, _activeRow))
                {
                    _activeRow = defaultRow;
                    _activeItem = defaultRow.Item;
                    _scrollToActiveItem = selectedRow is not null;
                }

                _activeItemIsDefault = true;
            }
        }

        private TreeViewItemContext<T>? FindRecoveryRow(TreeViewItemContext<T>? previousRow)
        {
            var rows = _projection.Rows;
            if (previousRow is null)
            {
                return null;
            }

            var previousParent = previousRow.Parent;
            var remainsInPreviousHierarchy = previousParent is null
                ? Items?.Any(item => ReferenceEquals(item, previousRow.Item)) == true
                : previousParent.Item.Children?.Any(item => ReferenceEquals(item, previousRow.Item)) == true;
            if (remainsInPreviousHierarchy)
            {
                for (var ancestor = previousParent; ancestor is not null; ancestor = ancestor.Parent)
                {
                    if (_projection.Find(ancestor.Item) is { } visibleAncestor)
                    {
                        return visibleAncestor;
                    }
                }
            }

            return rows.Count == 0
                ? null
                : rows[Math.Clamp(previousRow.Index, 0, rows.Count - 1)];
        }

        private string GetEffectiveElementId()
        {
            if (UserAttributes.TryGetValue("id", out var idValue) && idValue is not null)
            {
                var id = idValue.ToString();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    return id;
                }
            }

            return _treeElementId;
        }

        internal string GetItemElementId(TreeViewItemContext<T> row) => $"{GetEffectiveElementId()}_item_{row.Index}";

        private string? GetActiveDescendantId()
        {
            return IsVirtualized && ActiveRow is { } row ? GetItemElementId(row) : null;
        }

        private float GetEffectiveItemSize() => ItemSize > 0 ? ItemSize : Dense ? DefaultDenseItemSize : DefaultItemSize;

        internal TreeViewServerLoadEntry GetServerLoadEntry(ITreeItemData<T> item) => _serverLoadState.GetEntry(item);

        /// <summary>
        /// Returns whether a row owns <c>aria-activedescendant</c>.
        /// </summary>
        internal bool IsActiveItem(TreeViewItemContext<T> row) => ActiveRow is { } activeRow && activeRow.RowKey.Equals(row.RowKey);

        private MudTreeViewItem<T>? FindRenderedItem(TreeViewItemContext<T> row)
        {
            return _childItems.FirstOrDefault(item => item.CurrentItemContext is { } context && context.RowKey.Equals(row.RowKey));
        }

        /// <summary>
        /// Makes a row the active descendant.
        /// </summary>
        /// <param name="row">The row to activate.</param>
        /// <param name="scrollIntoView">Whether the row is scrolled into view after the next render.</param>
        private void SetActiveItem(TreeViewItemContext<T> row, bool scrollIntoView)
        {
            _pendingActivation = null;
            _scrollToActiveItem = scrollIntoView;
            _activeItemIsDefault = false;
            if (IsActiveItem(row))
            {
                return;
            }

            _activeItem = row.Item;
            _activeRow = row;
            StateHasChanged();
        }

        /// <summary>
        /// Handles a pointer interaction with a rendered row: the row becomes active and keyboard focus returns to the tree.
        /// </summary>
        internal async Task OnItemPointerInteractionAsync(MudTreeViewItem<T> item)
        {
            if (!IsVirtualized || Disabled || item.CurrentItemContext is not { } row)
            {
                return;
            }

            SetActiveItem(row, scrollIntoView: false);
            // Row chrome is not a tab stop, so a click on it returns focus to the tree. A tab stop rendered inside the row
            // by the item template (an input, a button) keeps the focus it just received.
            await JSRuntime.InvokeVoidAsyncIgnoreErrors("mudElementRef.focusUnlessDescendantFocused", _treeElement);
        }

        /// <summary>
        /// Handles a rendered row: the active row is scrolled into view and a queued keyboard activation is run once its row exists.
        /// </summary>
        internal async Task OnItemRenderedAsync(MudTreeViewItem<T> item)
        {
            if (_isDisposed || !IsVirtualized || item.CurrentItemContext is not { } row || !IsActiveItem(row))
            {
                return;
            }

            if (_scrollToActiveItem)
            {
                _scrollToActiveItem = false;
                await ScrollManager.ScrollToVirtualizedItemAsync(GetEffectiveElementId(), row.Index, GetEffectiveItemSize(), GetItemElementId(row));
            }

            if (_pendingActivation is { } pending && ReferenceEquals(pending.Item, row.Item))
            {
                _pendingActivation = null;
                await (pending.SelectOnly ? item.SelectFromKeyboardAsync() : item.ActivateFromKeyboardAsync());
            }
        }

        private async Task ScrollActiveItemIntoViewAsync()
        {
            if (ActiveRow is not { } row)
            {
                _scrollToActiveItem = false;
                return;
            }

            if (FindRenderedItem(row) is not null)
            {
                _scrollToActiveItem = false;
            }

            // When the row is not rendered yet the scroll is repeated on every render until it materializes.
            await ScrollManager.ScrollToVirtualizedItemAsync(GetEffectiveElementId(), row.Index, GetEffectiveItemSize(), GetItemElementId(row));
        }

        private bool CanHandleKeys() => IsVirtualized && !Disabled && Rows.Count > 0;

        private Task MoveActiveItemAsync(Func<ReadOnlyCollection<TreeViewItemContext<T>>, TreeViewItemContext<T>?, TreeViewItemContext<T>?> navigate)
        {
            var rows = Rows;
            var target = navigate(rows, ActiveRow);
            if (target is not null)
            {
                SetActiveItem(target, scrollIntoView: true);
            }

            return Task.CompletedTask;
        }

        private async Task HandleArrowRightAsync()
        {
            _pendingActivation = null;
            if (ActiveRow is not { } row)
            {
                return;
            }

            var item = FindRenderedItem(row);
            if (item is null)
            {
                _scrollToActiveItem = true;
                StateHasChanged();
                return;
            }

            if (!item.IsExpanded())
            {
                if (item.CanExpandFromKeyboard())
                {
                    await item.SetExpandedFromKeyboardAsync(true);
                }
                return;
            }

            if (ActiveRow is { } expandedRow && _projection.FindFirstChild(expandedRow) is { } firstChild)
            {
                SetActiveItem(firstChild, scrollIntoView: true);
            }
        }

        private async Task HandleArrowLeftAsync()
        {
            _pendingActivation = null;
            if (ActiveRow is not { } row)
            {
                return;
            }

            var item = FindRenderedItem(row);
            if (item?.CanExpandFromKeyboard() == true && item.IsExpanded())
            {
                await item.SetExpandedFromKeyboardAsync(false);
                return;
            }

            if (row.Parent is not null)
            {
                SetActiveItem(row.Parent, scrollIntoView: true);
            }
        }

        private async Task ActivateActiveItemAsync(bool selectOnly)
        {
            if (ActiveRow is not { } row)
            {
                return;
            }

            var item = FindRenderedItem(row);
            if (item is not null)
            {
                _pendingActivation = null;
                await (selectOnly ? item.SelectFromKeyboardAsync() : item.ActivateFromKeyboardAsync());
                return;
            }

            // The row is scrolled out of the render window: bring it back and activate it once it renders.
            _pendingActivation = (row.Item, selectOnly);
            _scrollToActiveItem = true;
            StateHasChanged();
        }

        private async Task UpdateKeyInterceptorAsync()
        {
            var updateVersion = ++_keyInterceptorUpdateVersion;
            if (_isDisposed)
            {
                return;
            }

            var effectiveElementId = GetEffectiveElementId();
            if (!IsVirtualized || Disabled)
            {
                if (!string.IsNullOrEmpty(_subscribedElementId))
                {
                    var subscribedElementId = _subscribedElementId;
                    _subscribedElementId = null;
                    await KeyInterceptorService.UnsubscribeAsync(subscribedElementId);
                }

                return;
            }

            if (string.Equals(_subscribedElementId, effectiveElementId, StringComparison.Ordinal))
            {
                return;
            }

            if (!string.IsNullOrEmpty(_subscribedElementId))
            {
                var subscribedElementId = _subscribedElementId;
                _subscribedElementId = null;
                await KeyInterceptorService.UnsubscribeAsync(subscribedElementId);
                if (_isDisposed || updateVersion != _keyInterceptorUpdateVersion)
                {
                    return;
                }
            }

            var options = new KeyInterceptorOptions
            {
                // Keys are handled only while the tree element itself has focus; a tab stop rendered inside a row by the
                // item template keeps its own keyboard behavior.
                IgnoreDescendantEvents = true,
                Keys =
                [
                    new(" ", subscribeDown: true, preventDown: "key+none", preventUp: "key+none"),
                    new("ArrowDown", subscribeDown: true, preventDown: "key+none"),
                    new("ArrowUp", subscribeDown: true, preventDown: "key+none"),
                    new("ArrowRight", subscribeDown: true, preventDown: "key+none"),
                    new("ArrowLeft", subscribeDown: true, preventDown: "key+none"),
                    new("Home", subscribeDown: true, preventDown: "key+none"),
                    new("End", subscribeDown: true, preventDown: "key+none"),
                    new("Enter", subscribeDown: true, preventDown: "key+none"),
                    new("NumpadEnter", subscribeDown: true, preventDown: "key+none")
                ]
            };

            await KeyInterceptorService.SubscribeAsync(effectiveElementId, options, keys => keys
                .When(CanHandleKeys, builder => builder
                    .OnKeyDown("ArrowDown", () => MoveActiveItemAsync(static (rows, current) => rows[Math.Clamp((current?.Index ?? -1) + 1, 0, rows.Count - 1)]))
                    .OnKeyDown("ArrowUp", () => MoveActiveItemAsync(static (rows, current) => rows[Math.Clamp((current?.Index ?? rows.Count) - 1, 0, rows.Count - 1)]))
                    .OnKeyDown("Home", () => MoveActiveItemAsync(static (rows, _) => rows[0]))
                    .OnKeyDown("End", () => MoveActiveItemAsync(static (rows, _) => rows[^1]))
                    .OnKeyDown("ArrowRight", HandleArrowRightAsync)
                    .OnKeyDown("ArrowLeft", HandleArrowLeftAsync)
                    .OnKeyDown(" ", () => ActivateActiveItemAsync(selectOnly: true))
                    .OnKeyDownAny(["Enter", "NumpadEnter"], () => ActivateActiveItemAsync(selectOnly: false))));

            if (_isDisposed
                || updateVersion != _keyInterceptorUpdateVersion
                || !IsVirtualized
                || Disabled
                || !string.Equals(effectiveElementId, GetEffectiveElementId(), StringComparison.Ordinal))
            {
                if (!string.Equals(_subscribedElementId, effectiveElementId, StringComparison.Ordinal))
                {
                    await KeyInterceptorService.UnsubscribeAsync(effectiveElementId);
                }
                return;
            }

            _subscribedElementId = effectiveElementId;
        }

        #endregion

        internal async Task OnItemClickAsync(MudTreeViewItem<T> clickedItem)
        {
            if (ReadOnly)
            {
                return;
            }
            if (MultiSelection)
            {
                if (IsVirtualized && clickedItem.CurrentItemContext is { } clickedRow)
                {
                    _ = Rows;
                    var row = _projection.FindRow(clickedRow.RowKey) ?? clickedRow;
                    _selection = new HashSet<T>(_projection.ToggleSubtreeSelection(row, _selection, AutoSelectParent), Comparer);
                    await _selectedValuesState.SetValueAsync(_selection.ToList()); // note: .ToList() is essential here!
                    await UpdateItemsAsync(synchronizeVirtualized: false);
                    return;
                }

                var items = clickedItem.GetChildItemsRecursive();
                items.Add(clickedItem!);
                var allSelected = items.All(x => x.GetState<bool>(nameof(MudTreeViewItem<T>.Selected)));
                // toggle selection of the clickedItem and its children
                foreach (var item in items.Where(x => x.GetValue() is not null))
                {
                    if (allSelected)
                    {
                        _selection.Remove(item.GetValue()!);
                    }
                    else
                    {
                        _selection.Add(item.GetValue()!);
                    }
                }
                if (AutoSelectParent)
                {
                    UpdateParentItem(clickedItem.Parent);
                }
                await _selectedValuesState.SetValueAsync(_selection.ToList()); // note: .ToList() is essential here!
                await UpdateItemsAsync();
                return;
            }
            var selected = clickedItem.IsSelected();
            if (ToggleSelection)
            {
                await SetSelectedValueAsync(selected ? default : clickedItem.GetValue()); // <-- toggle selected value
            }
            else if (!selected)
            {
                // SingleSelection
                await SetSelectedValueAsync(clickedItem.GetValue());
            }
        }

        /// <summary>
        /// This changes the parent item's state based on the selection state of its children in multi-selection mode
        /// But only if the items are clicked, not when the selection is modified via SelectedValues
        /// </summary>
        private void UpdateParentItem(MudTreeViewItem<T>? parentItem)
        {
            while (parentItem is not null)
            {
                var parentValue = parentItem.GetValue();
                if (parentValue is not null)
                {
                    var parentSelected = parentItem.ChildItems.Select(x => x.GetValue()).Where(x => x is not null).All(x => _selection.Contains(x!));
                    if (parentSelected)
                    {
                        _selection.Add(parentValue);
                    }
                    else
                    {
                        _selection.Remove(parentValue);
                    }
                }
                parentItem = parentItem.Parent;
            }
        }

        internal async Task AddChildAsync(MudTreeViewItem<T> item)
        {
            _childItems.Add(item);
            // this is to ensure that setting Selected="true" on the item will update the single/multiselection.
            // Note: Setting Selected="false" has no effect however because it would cancel the initialization of the SelectedValue or SelectedValues !
            var value = item.GetValue();
            if (value is not null && item.GetState<bool>(nameof(MudTreeViewItem<T>.Selected)))
            {
                await SelectAsync(value);
            }
            await item.UpdateSelectionStateAsync(GetSelection());
        }

        internal void RemoveChild(MudTreeViewItem<T> item)
        {
            _childItems.Remove(item);
        }

        internal async Task SelectAsync(T value)
        {
            if (MultiSelection)
            {
                _selection.Add(value);
                if (!_isFirstRender)
                {
                    var shouldRefresh = ApplyVirtualizedAutoExpand(_selection);
                    await _selectedValuesState.SetValueAsync(_selection.ToList()); // note: .ToList() is essential here!
                    await UpdateItemsAsync();
                    if (shouldRefresh)
                    {
                        RefreshProjection();
                    }
                }
                return;
            }
            // single and toggle selection
            await _selectedValueState.SetValueAsync(value);
            if (!_isFirstRender)
            {
                var shouldRefresh = ApplyVirtualizedAutoExpand(GetSelection());
                await UpdateItemsAsync();
                if (shouldRefresh)
                {
                    RefreshProjection();
                }
            }
        }

        internal async Task UnselectAsync(T value)
        {
            if (_isFirstRender || !MultiSelection)
            {
                return;
            }
            _selection.Remove(value);
            await _selectedValuesState.SetValueAsync(_selection.ToList()); // note: .ToList() is essential here!
            if (IsVirtualized)
            {
                await UpdateItemsAsync();
            }
        }

        ///  <summary>
        ///  Sets the selected value of the tree view in Single- and ToggleSelection mode.
        ///  If the value is found, the corresponding item is selected;
        ///  otherwise, selected value is set default.
        ///  If the selected item is valid it sets the corresponding tree item to selected.
        ///  </summary>
        ///  <param name="value">The value to be set as the selected value.</param>
        internal async Task SetSelectedValueAsync(T? value)
        {
            bool isValid;
            var synchronizedVirtualSelection = IsVirtualized;
            if (IsVirtualized)
            {
                var requestedSelection = new HashSet<T>(Comparer);
                if (value is not null)
                {
                    requestedSelection.Add(value);
                }

                var representedSelection = _projection.SynchronizeSelection(Items, requestedSelection, Comparer);
                isValid = value is not null && representedSelection.Count > 0;
            }
            else
            {
                isValid = value != null && GetSelectableValues().Contains(value);
            }

            // note: if there is no item that corresponds to the value, the value is reset to default!
            await _selectedValueState.SetValueAsync(isValid ? value : default);
            var shouldRefresh = ApplyVirtualizedAutoExpand(GetSelection(), useBackingItems: synchronizedVirtualSelection);
            await UpdateItemsAsync(synchronizeVirtualized: !synchronizedVirtualSelection);
            if (shouldRefresh)
            {
                RefreshProjection();
            }
        }

        ///  <summary>
        ///  Sets the selected values of the tree view in MultiSelection mode.
        /// Discard any values which are not represented by child values.
        ///  </summary>
        private async Task SetSelectedValuesAsync(IReadOnlyCollection<T> newValues)
        {
            var synchronizedVirtualSelection = IsVirtualized;
            var newSelection = synchronizedVirtualSelection
                ? new HashSet<T>(_projection.SynchronizeSelection(Items, newValues, Comparer), Comparer)
                : new HashSet<T>(newValues.Where(GetSelectableValues().Contains), Comparer);
            if (_selection.SetEquals(newSelection))
            {
                return;
            }
            _selection = newSelection;
            await _selectedValuesState.SetValueAsync(newSelection);
            var shouldRefresh = ApplyVirtualizedAutoExpand(_selection, useBackingItems: synchronizedVirtualSelection);
            await UpdateItemsAsync(synchronizeVirtualized: !synchronizedVirtualSelection);
            if (shouldRefresh)
            {
                RefreshProjection();
            }
        }

        /// <summary>
        /// Let the items update their selection state visualization and state according to
        /// the selection in the tree view
        /// </summary>
        private async Task UpdateItemsAsync(bool synchronizeVirtualized = true)
        {
            var selection = GetSelection();
            if (IsVirtualized && synchronizeVirtualized)
            {
                _projection.SynchronizeSelection(Items, selection, Comparer);
            }

            foreach (var item in _childItems)
            {
                await item.UpdateSelectionStateAsync(selection);
            }

            RefreshProjection();
        }

        /// <summary>
        /// Applies backing <see cref="ITreeItemData{T}.Selected"/> changes made outside the tree to the selected values.
        /// </summary>
        /// <returns>The reconciled selection and whether the selected values or the backing items changed.</returns>
        private async Task<TreeViewSelectionResult<T>> ReconcileVirtualizedSelectionAsync()
        {
            _ = Rows;
            var result = _projection.ReconcileSelection(
                MultiSelection,
                _selectedValueState.Value,
                _selection);
            if (MultiSelection)
            {
                if (result.SelectionChanged)
                {
                    _selection = new HashSet<T>(result.SelectedValues, Comparer);
                    await _selectedValuesState.SetValueAsync(_selection.ToList());
                }
            }
            else if (result.SelectionChanged)
            {
                await _selectedValueState.SetValueAsync(result.SelectedValue);
            }

            return result;
        }

        private HashSet<T> GetSelection()
        {
            HashSet<T> selection;
            if (MultiSelection)
            {
                selection = new HashSet<T>(_selection, Comparer);
            }
            else
            {
                selection = new HashSet<T>(Comparer);
                if (_selectedValueState.Value != null)
                {
                    selection.Add(_selectedValueState.Value);
                }
            }
            return selection;
        }

        private HashSet<T> GetSelectableValues()
        {
            if (ItemTemplate is not null && Items is not null)
            {
                return GetItemValuesRecursive(Items);
            }

            return GetChildValuesRecursive();
        }

        // TODO: speed this up with caching
        private HashSet<T> GetItemValuesRecursive(IEnumerable<ITreeItemData<T>> items, HashSet<T>? values = null)
        {
            values ??= new HashSet<T>(Comparer);

            foreach (var item in items)
            {
                var value = TreeViewHierarchy<T>.GetItemValue(item);
                if (value is not null)
                {
                    values.Add(value);
                }

                if (item.Children is not null && item.Children.Count > 0)
                {
                    GetItemValuesRecursive(item.Children, values);
                }
            }

            return values;
        }

        // TODO: speed this up with caching
        private HashSet<T> GetChildValuesRecursive(IEnumerable<MudTreeViewItem<T>>? children = null, HashSet<T>? values = null)
        {
            values ??= new HashSet<T>(Comparer);
            children ??= _childItems;

            foreach (var item in children)
            {
                var value = item.GetValue();
                if (value is not null)
                {
                    values.Add(value);
                }

                if (item.ChildItems.Count > 0)
                {
                    GetChildValuesRecursive(item.ChildItems, values);
                }
            }

            return values;
        }

        /// <summary>
        /// Expands the ancestors of selected items when <see cref="AutoExpand"/> is set.
        /// </summary>
        /// <param name="selection">The selected values.</param>
        /// <param name="useBackingItems">Whether to traverse the backing data instead of the last projection, because the data changed since it was built.</param>
        private bool ApplyVirtualizedAutoExpand(HashSet<T> selection, bool useBackingItems = false)
        {
            if (!IsVirtualized || !AutoExpand || selection.Count == 0)
            {
                return false;
            }

            if (useBackingItems)
            {
                return TreeViewHierarchy<T>.AutoExpand(Items, selection, Comparer);
            }

            _ = Rows;
            return _projection.AutoExpand(selection);
        }

        /// <summary>
        /// Releases resources used by this component.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _keyInterceptorUpdateVersion++;
            if (IsJSRuntimeAvailable && !string.IsNullOrEmpty(_subscribedElementId))
            {
                var subscribedElementId = _subscribedElementId;
                _subscribedElementId = null;
                await KeyInterceptorService.UnsubscribeAsync(subscribedElementId);
            }

            GC.SuppressFinalize(this);
        }
    }
}
