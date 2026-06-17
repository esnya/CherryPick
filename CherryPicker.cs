using FrooxEngine;
using Elements.Core;
using FrooxEngine.UIX;
using System.Reflection;
using static CherryPick.CherryPick;

namespace CherryPick;

public class CherryPicker(ComponentSelector selector, Slot searchRoot, Slot componentUIRoot, ButtonEventHandler<string> onGenericPressed, ButtonEventHandler<string> onAddPressed, UIBuilder searchBuilder, Sync<string> scope)
{
    private static readonly string PROTOFLUX_PREFIX = "/ProtoFlux/Runtimes/";
    private const long CONCRETE_GENERIC_ORDER_START = -4096;
    private const long RECENT_COMPONENT_ORDER_START = -3072;
    private static readonly colorX RecentComponentColor = RadiantUI_Constants.Sub.PURPLE;
    public string Scope
    {
        get => scope.Value;
        set => scope.Value = value;
    }
    public static bool IsReady { get; private set; }
    public static WorkerDetails[] Workers => _allWorkers;
    private readonly SortedList<float, WorkerDetails> _results = new(new MatchRatioComparer()); // Not queryable by index due to the implementation of MatchRatioComparer
    private static readonly WorkerDetails[] _allWorkers = [];
    private static readonly List<WorkerDetails> _recentComponents = [];
    private static readonly Lazy<Type[]> _genericArgumentTypes = new(BuildGenericArgumentTypes);
    private static readonly (string Name, Type Type)[] _knownGenericArgumentAliases =
    [
        ("bool", typeof(bool)),
        ("byte", typeof(byte)),
        ("sbyte", typeof(sbyte)),
        ("short", typeof(short)),
        ("ushort", typeof(ushort)),
        ("int", typeof(int)),
        ("uint", typeof(uint)),
        ("long", typeof(long)),
        ("ulong", typeof(ulong)),
        ("float", typeof(float)),
        ("double", typeof(double)),
        ("decimal", typeof(decimal)),
        ("char", typeof(char)),
        ("string", typeof(string)),
        ("Uri", typeof(Uri))
    ];



    static CherryPicker()
    {
        static IEnumerable<CategoryNode<Type>> flatten(IEnumerable<CategoryNode<Type>> categories) =>
            categories
            .SelectMany(category => flatten(category.Subcategories))
            .Concat(categories);


        IEnumerable<CategoryNode<Type>> allCategories = flatten(WorkerInitializer.ComponentLibrary.Subcategories);
        List<WorkerDetails> details = [];

        // using FileStream compFile = File.OpenWrite("./COMPONENTS.txt");
        // using StreamWriter writer = new(compFile);

        foreach (var category in allCategories)
        {
            foreach (var element in category.Elements)
            {
                if (element.IsDataModelType())
                {
                    string elementName = element.GetNiceName();
                    string elementPath = category.GetPath();

                    // writer.WriteLine($"{elementName}\n{elementPath}\n");
                    WorkerDetails detail = new(elementName, elementPath, element);
                    details.Add(detail);
                }
            }
        }

        _allWorkers = [.. details];
    }



    public static void SetReady() => IsReady = true;



    #region String matching



    public void PerformMatch(string query, int resultCount = 10, bool showProtofluxComponents = true)
    {
        ResetResults(resultCount);

        int workerCount = Workers.Length;
        WorkerDetails[] details = Workers;

        string[] splitQuery = query.Split(' ');



        // The for loops are a bit hot and can cause minor
        // hitches if care isn't taken. Avoiding branch logic if possible

        // Check if there's actually anything to filter, because if there isn't, a slightly more efficient loop can be used
        if (string.IsNullOrEmpty(Scope) && showProtofluxComponents)
        {
            for (int i = 0; i < workerCount; i++)
            {
                WorkerDetails worker = details[i];
                float ratio = CherryPick_Helper.MatchRatioInsensitive(worker.LowerName, splitQuery);

                _results.Add(ratio, worker);
                int detailCount = _results.Count;

                _results.RemoveAt(detailCount - 1);
            }
        }
        else
        {
            string searchScope = "/" + Scope;
            for (int i = 0; i < workerCount; i++)
            {
                WorkerDetails worker = details[i];
                float ratio = worker.Path.StartsWith(searchScope) && (showProtofluxComponents || !worker.Path.StartsWith(PROTOFLUX_PREFIX))
                    ? CherryPick_Helper.MatchRatioInsensitive(worker.LowerName, splitQuery)
                    : 0f;

                _results.Add(ratio, worker);
                int detailCount = _results.Count;

                _results.RemoveAt(detailCount - 1);
            }
        }


        // Remove the zero-scored results after the fact. Avoids another conditional in the hot path above
        while (MathX.Approximately(_results.LastOrDefault().Key, 0f) && _results.Count > 0)
            _results.RemoveAt(_results.Count - 1);

    }



    void ResetResults(int startCount = 10)
    {
        _results.Clear();
        for (int i = 0; i < startCount; i++)
            _results.Add(0f, default);
    }

    #endregion



    #region TextEditor Events



    // public void EditStart(TextEditor editor)
    // {
    //     if (componentUIRoot != null && searchRoot != null)
    //     {
    //         componentUIRoot.ActiveSelf = false;
    //         searchRoot.ActiveSelf = true;
    //     }
    // }



    // public void EditFinished(TextEditor editor)
    // {
    //     if (editor != null &&
    //         editor.Text.Target != null &&
    //         string.IsNullOrEmpty(editor.Text.Target.Text) &&
    //         componentUIRoot != null &&
    //         searchRoot != null)
    //     {
    //         componentUIRoot.ActiveSelf = true;
    //         searchRoot.ActiveSelf = false;
    //     }
    // }



    public void OpenPickerView()
    {
        componentUIRoot.ActiveSelf = false;
        searchRoot.ActiveSelf = true;
    }

    public void ClosePickerView()
    {
        componentUIRoot.ActiveSelf = true;
        searchRoot.ActiveSelf = false;
    }

    public static void ClearSearch(TextEditor editor)
    {
        editor.Text.Target.Text = null!;
    }



    public void EditChanged(TextEditor editor)
    {
        if (searchRoot == null ||
            componentUIRoot == null || 
            editor == null ||
            onGenericPressed == null ||
            onAddPressed == null || 
            searchBuilder == null ||
            !IsReady) // You can't search until the cache is built! This is fine in most cases, but if you end up searching before then, too bad!
                return;


        string txt = editor.Text.Target.Text;
        if (string.IsNullOrEmpty(txt))
        {
            ClosePickerView();
            return;
        }

        OpenPickerView();


        int genericStart = txt.IndexOf('<');
        int genericEnd = txt.LastIndexOf('>');
        string? matchTxt = null;
        string? genericType = null;

        if (genericStart > 0)
        {
            matchTxt = txt.Substring(0, genericStart);

            if (genericEnd > genericStart)
                genericType = txt.Substring(genericStart + 1, genericEnd - genericStart - 1);
            else if (genericStart < txt.Length - 1)
                genericType = txt.Substring(genericStart + 1);
        }
        else
        {
            matchTxt = txt;
        }


        // searchRoot.DestroyChildren();
        int resultCount = Config!.GetValue(ResultCount);
        resultCount = MathX.Min(resultCount, MAX_RESULT_COUNT);

        // Three different possibilities:
        // 1. ProtoFlux Search (Scope is non-empty): Show ProtoFlux (obviously)
        // 2. Component Search (Scope is empty), ProtoFlux is hidden: Don't show ProtoFlux
        // 3. Component Search (Scope is empty), ProtoFlux should be shown (debug mode?): Show ProtoFlux
        bool showProtofluxComponents = !string.IsNullOrEmpty(Scope) || Config!.GetValue(ShowProtofluxInComponentSearch);

        PerformMatch(matchTxt, resultCount, showProtofluxComponents);

        ClearPinnedResults();

        HashSet<Type> pinnedTypes = BuildRecentComponentResults(editor);

        if (!string.IsNullOrEmpty(genericType))
            BuildConcreteGenericResults(genericType, editor, pinnedTypes);

        for (int i = 0; i < searchRoot.ChildrenCount; i++)
        {
            if (IsPinnedResult(searchRoot[i].OrderOffset))
                continue;

            if (!_results.Any(r => r.Value.Name == searchRoot[i].Name))
            {
                searchRoot[i].ActiveSelf = false;
                searchRoot.World.RunInUpdates(1, searchRoot[i].Destroy);
            }
        }

        int j = 0;
        foreach (var result in _results.Values)
        {
            bool isGenType = result.Type.IsGenericTypeDefinition;
            string arg = "";

            Slot? existingMatch = searchRoot.FindChild(s => s.Name == result.Name && !IsPinnedResult(s.OrderOffset));
            if (existingMatch is not null)
            {
                existingMatch.OrderOffset = j++;
                continue;
            }

            Slot? buttonSlot = null;
            try
            {
                arg = isGenType ? Path.Combine(result.Path, result.Type.AssemblyQualifiedName) : searchRoot.World.Types().EncodeType(result.Type);
                var pressed = isGenType ? onGenericPressed : onAddPressed;
                Action? localPressed = isGenType ? null : () => RememberRecentComponent(result);
                buttonSlot = CreateButton(result, pressed, arg, searchBuilder, editor, RadiantUI_Constants.Sub.CYAN, localPressed).Slot;
            }
            catch (ArgumentException)
            {
                CherryPick.Warn($"Tried to encode a non-data model type: {result.Type}");
            }
            j++;
        }
    }



    #endregion



    #region Generic concrete results



    private void ClearPinnedResults()
    {
        for (int i = searchRoot.ChildrenCount - 1; i >= 0; i--)
        {
            if (IsPinnedResult(searchRoot[i].OrderOffset))
            {
                searchRoot[i].ActiveSelf = false;
                searchRoot.World.RunInUpdates(1, searchRoot[i].Destroy);
            }
        }
    }



    private static bool IsPinnedResult(long orderOffset) => orderOffset >= CONCRETE_GENERIC_ORDER_START && orderOffset < 0;



    private HashSet<Type> BuildRecentComponentResults(TextEditor editor)
    {
        HashSet<Type> displayedTypes = [];
        int recentCount = Math.Clamp(Config!.GetValue(RecentComponentCount), 0, MAX_RESULT_COUNT);
        if (recentCount <= 0)
            return displayedTypes;

        WorkerDetails[] recentComponents;
        lock (_recentComponents)
            recentComponents = [.. _recentComponents.Take(recentCount)];

        for (int i = 0; i < recentComponents.Length; i++)
        {
            WorkerDetails detail = recentComponents[i];
            if (detail.Type is null || detail.Type.IsGenericTypeDefinition || !displayedTypes.Add(detail.Type))
                continue;

            Button typeButton = CreateConcreteComponentButton(detail, editor, RecentComponentColor);
            typeButton.Slot.OrderOffset = RECENT_COMPONENT_ORDER_START + i;
        }

        return displayedTypes;
    }



    private void BuildConcreteGenericResults(string genericType, TextEditor editor, HashSet<Type> pinnedTypes)
    {
        Type? genParam = ResolveGenericArgument(genericType);
        if (genParam is null)
            return;

        foreach (var result in _results.Values)
        {
            if (result.Type is null ||
                !result.Type.IsGenericTypeDefinition ||
                result.Type.GetGenericArguments().Length != 1 ||
                !TryConstructGeneric(result.Type, genParam, out Type? constructed) ||
                constructed is null ||
                pinnedTypes.Contains(constructed))
            {
                continue;
            }

            WorkerDetails detail = new(constructed.GetNiceName(), result.Path, constructed);
            Button typeButton = CreateConcreteComponentButton(detail, editor, RadiantUI_Constants.Sub.ORANGE);
            typeButton.Slot.OrderOffset = CONCRETE_GENERIC_ORDER_START;

            pinnedTypes.Add(constructed);
            break;
        }
    }



    private Type? ResolveGenericArgument(string genericType)
    {
        Type? exact = TryParseNiceType(genericType);
        if (exact is not null)
            return exact;

        string trimmed = genericType.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        Type? aliasMatch = FindKnownGenericArgumentAlias(trimmed);
        if (aliasMatch is not null)
            return aliasMatch;

        return _genericArgumentTypes.Value
            .Select(t => new { Type = t, Rank = GetGenericArgumentMatchRank(t, trimmed) })
            .Where(t => t.Rank >= 0)
            .OrderBy(t => t.Rank)
            .ThenBy(t => t.Type.GetNiceName().Length)
            .ThenBy(t => t.Type.GetNiceName(), StringComparer.OrdinalIgnoreCase)
            .Select(t => t.Type)
            .FirstOrDefault();
    }



    private static Type? FindKnownGenericArgumentAlias(string query)
    {
        return _knownGenericArgumentAliases
            .Where(alias => alias.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(alias => alias.Name.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(alias => alias.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(alias => alias.Name.Length)
            .Select(alias => alias.Type)
            .FirstOrDefault();
    }



    private Type? TryParseNiceType(string genericType)
    {
        try
        {
            return searchRoot.World.Types.ParseNiceType(genericType, true);
        }
        catch (Exception)
        {
            return null;
        }
    }



    private static bool TryConstructGeneric(Type genericDefinition, Type genericArgument, out Type? constructed)
    {
        constructed = null;
        try
        {
            Type type = genericDefinition.MakeGenericType(genericArgument);
            if ((bool?)type.GetProperty("IsValidGenericType", BindingFlags.Static | BindingFlags.Public)?.GetValue(null) == false ||
                !type.IsValidGenericType(validForInstantiation: true))
            {
                return false;
            }

            constructed = type;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }



    private bool TryEncodeType(Type type, out string arg, bool warn = true)
    {
        arg = "";
        try
        {
            arg = searchRoot.World.Types().EncodeType(type);
            return true;
        }
        catch (ArgumentException)
        {
            if (warn)
                CherryPick.Warn($"Tried to encode a non-data model type: {type}");
            return false;
        }
    }



    private static Type[] BuildGenericArgumentTypes()
    {
        List<Type> types = [];
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
                continue;

            try
            {
                types.AddRange(assembly.GetExportedTypes().Where(IsGenericArgumentCandidate));
            }
            catch (Exception ex)
            {
                CherryPick.Warn($"Failed to inspect exported types from {assembly.FullName}: {ex}");
            }
        }

        return [.. types.Distinct()];
    }



    private static bool IsGenericArgumentCandidate(Type type)
    {
        if (type.ContainsGenericParameters || type.IsGenericParameter || type.IsGenericTypeDefinition)
            return false;

        return type.IsDataModelType() ||
            typeof(IWorldElement).IsAssignableFrom(type) ||
            typeof(IWorker).IsAssignableFrom(type) ||
            typeof(IAsset).IsAssignableFrom(type);
    }



    private static int GetGenericArgumentMatchRank(Type type, string query)
    {
        string niceName = type.GetNiceName();
        string name = type.Name;
        string? fullName = type.FullName;

        if (Matches(niceName, query, StringComparison.OrdinalIgnoreCase) ||
            Matches(name, query, StringComparison.OrdinalIgnoreCase) ||
            (fullName is not null && Matches(fullName, query, StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        if (StartsWith(niceName, query) || StartsWith(name, query) || (fullName is not null && StartsWith(fullName, query)))
            return 1;

        if (Contains(niceName, query) || Contains(name, query) || (fullName is not null && Contains(fullName, query)))
            return 2;

        return -1;
    }



    private static bool Matches(string value, string query, StringComparison comparison) => value.Equals(query, comparison);
    private static bool StartsWith(string value, string query) => value.StartsWith(query, StringComparison.OrdinalIgnoreCase);
    private static bool Contains(string value, string query) => value.Contains(query, StringComparison.OrdinalIgnoreCase);



    #endregion



    #region Component selection and history



    private Button CreateConcreteComponentButton(in WorkerDetails detail, TextEditor editor, colorX col)
    {
        WorkerDetails capturedDetail = detail;
        if (TryEncodeType(capturedDetail.Type, out string arg, warn: false))
            return CreateButton(capturedDetail, onAddPressed, arg, searchBuilder, editor, col, () => RememberRecentComponent(capturedDetail));

        return CreateButton(
            capturedDetail,
            null,
            "",
            searchBuilder,
            editor,
            col,
            () =>
            {
                RememberRecentComponent(capturedDetail);
                selector.ComponentSelected.Target?.Invoke(selector, capturedDetail.Type);
            });
    }



    private static void RememberRecentComponent(WorkerDetails detail)
    {
        if (detail.Type is null || detail.Type.IsGenericTypeDefinition)
            return;

        lock (_recentComponents)
        {
            _recentComponents.RemoveAll(recent => recent.Type == detail.Type);
            _recentComponents.Insert(0, detail);

            if (_recentComponents.Count > MAX_RESULT_COUNT)
                _recentComponents.RemoveRange(MAX_RESULT_COUNT, _recentComponents.Count - MAX_RESULT_COUNT);
        }
    }



    #endregion



    // One day we will have better UI construction... one day :')
    private Button CreateButton(in WorkerDetails detail, ButtonEventHandler<string>? pressed, string arg, UIBuilder builder, TextEditor editor, colorX col, Action? localPressed = null)
    {
        // Snip the scope off of the beginning of the path if the browser so that it's relative to the scope
        string path = Scope != null ? detail.Path.Replace("/" + Scope, null) : detail.Path;
        string buttonText = $"<noparse={detail.Name.Length}>{detail.Name}<br><size=61.803%><line-height=133%>{path}";


        var button = pressed is null
            ? builder.Button(buttonText, col)
            : builder.Button(buttonText, col, pressed, arg, PressDelay);
        ValueField<ulong> pressProxy = button.Slot.AddSlot("PressProxy").AttachComponent<ValueField<ulong>>();
        ButtonValueShift<ulong> proxyShifter = button.Slot.AttachComponent<ButtonValueShift<ulong>>();
        button.Slot.Name = detail.Name;

        proxyShifter.Delta.Value = 1;
        proxyShifter.TargetValue.Target = pressProxy.Value;

        ValueField<double> lastPressed = button.Slot.AddSlot("LastPressed").AttachComponent<ValueField<double>>();
        button.ClearFocusOnPress.Value = Config!.GetValue(CherryPick.ClearFocus);

        bool isGenericTypeDefinition = detail.Type.IsGenericTypeDefinition;
        if (isGenericTypeDefinition || localPressed is not null)
        {
            // Define delegate here for unsubscription later
            void CherryPickButtonPress(IChangeable c)
            {
                double now = searchRoot.World.Time.WorldTime;


                if (now - lastPressed.Value < CherryPick.PressDelay || CherryPick.SingleClick)
                {
                    localPressed?.Invoke();

                    if (isGenericTypeDefinition)
                    {
                        ClosePickerView();
                        ClearSearch(editor);
                    }
                }
                else
                    lastPressed.Value.Value = now;
            }


            // // Destroy delegate for unsubscription
            // void ButtonDestroyed(IDestroyable d)
            // {
            //     IButton destroyedButton = (IButton)d;

            //     // When the button is destroyed, unsubscribe the events like a good boy
            //     destroyedButton.LocalPressed -= CherryPickButtonPress;
            //     destroyedButton.Destroyed -= ButtonDestroyed;
            // }

            void DestroyPressProxy(IDestroyable d)
            {
                pressProxy.Value.Changed -= CherryPickButtonPress;
                pressProxy.Destroyed -= DestroyPressProxy;
            }


            pressProxy.Value.Changed += CherryPickButtonPress;
            pressProxy.Destroyed += DestroyPressProxy;
        }


        // Funny magic UI numbers
        var text = (Text)button.LabelTextField.Parent;
        text.Size.Value = 24.44582f; 


        // Smooth the color transitions on the buttons for visual appeal
        var smooth = button.Slot.AttachComponent<SmoothValue<colorX>>();
        IField<colorX> target = button.ColorDrivers.First().ColorDrive.Target;
        smooth.TargetValue.Value = target.Value;


        button.ColorDrivers.First().ColorDrive.Target = smooth.TargetValue;
        smooth.Value.Target = target;
        smooth.Speed.Value = 12f;


        return button;
    }
}



// When using this in a SortedList, you won't be able to look up an entry by key!!
public readonly struct MatchRatioComparer : IComparer<float>
{
    public readonly int Compare(float x, float y)
    {
        return x > y ? -1 : 1;
    }
}
