using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

using Elements.Core;

using FrooxEngine;

using HarmonyLib;

using Renderite.Shared;

namespace CherryPick;


/// <summary>
/// Shared generic argument catalog and constraint-safe construction helpers.
/// </summary>
internal static class GenericTypeCatalog
{
    private const string ProtoFluxNodeNamespacePrefix = "FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes";
    private const string CollectionNodeNamespace = ProtoFluxNodeNamespacePrefix + ".Collections";

    private static readonly HashSet<string> _flowNodeNames =
    [
        "AsyncForEachObject",
        "AsyncForEachValue",
        "AsyncForEachWithIndexObject",
        "AsyncForEachWithIndexValue",
        "ForEachObject",
        "ForEachValue",
        "ForEachWithIndexObject",
        "ForEachWithIndexValue",
        "ReadOnlyForEachWithIndexObject",
        "ReadOnlyForEachWithIndexValue"
    ];

    private static readonly HashSet<string> _dictionaryPairNodeNames =
    [
        "UnpackObjectKeyObjectValuePair",
        "UnpackObjectKeyValueValuePair",
        "UnpackValueKeyObjectValuePair",
        "UnpackValueKeyValueValuePair"
    ];

    private static readonly HashSet<string> _singleArgumentAppendTargets =
    [
        ProtoFluxNodeNamespacePrefix + ".ObjectRelay`1",
        ProtoFluxNodeNamespacePrefix + ".ValueObjectInput`1",
        ProtoFluxNodeNamespacePrefix + ".RefObjectInput`1",
        ProtoFluxNodeNamespacePrefix + ".NullCoalesce`1",
        ProtoFluxNodeNamespacePrefix + ".MultiNullCoalesce`1",
        ProtoFluxNodeNamespacePrefix + ".Actions.FireOnObjectValueChange`1",
        ProtoFluxNodeNamespacePrefix + ".FrooxEngine.Variables.DataModelObjectFieldStore`1"
    ];

    private static readonly Type[] _genericCarrierDefinitions =
    [
        typeof(IEnumerable<>),
        typeof(ICollection<>),
        typeof(IList<>),
        typeof(IReadOnlyCollection<>),
        typeof(IReadOnlyList<>),
        typeof(IDictionary<,>)
    ];

    // Value collection nodes carry an unmanaged constraint, so they need a
    // dedicated set instead of falling back to the broader object catalog.
    private static readonly Type[] _unmanagedSeedTypes =
    [
        typeof(dummy),
        typeof(DummyEnum),
        typeof(Guid),
        typeof(bool),
        typeof(int),
        typeof(float),
        typeof(float2),
        typeof(float3),
        typeof(float4),
        typeof(floatQ),
        typeof(color),
        typeof(colorX),
        typeof(BodyNode)
    ];

    // Observed heterogeneous Data Model dictionaries. Keep this explicit rather
    // than projecting the full SeedTypes x SeedTypes cross product.
    private static readonly (Type Key, Type Value)[] _dictionaryProfiles =
    [
        (typeof(BodyNode), typeof(Slot)),
        (typeof(string), typeof(IWorldElement)),
        (typeof(string), typeof(PermissionSet))
    ];

    private static readonly Lazy<IReadOnlyList<Type>> _seedTypes = new(BuildSeedTypes);
    private static readonly Lazy<IReadOnlyList<Type>> _candidateTypes = new(BuildCandidateTypes);
    private static readonly Lazy<IReadOnlyList<Type>> _openInterfaceDefinitions = new(BuildOpenInterfaceDefinitions);

    /// <summary>
    /// The ordered scalar/object seed types used to project common generic candidates.
    /// </summary>
    internal static IReadOnlyList<Type> SeedTypes => _seedTypes.Value;

    /// <summary>
    /// The ordered seed, non-generic collection, and closed collection-interface catalog.
    /// </summary>
    internal static IReadOnlyList<Type> CandidateTypes => _candidateTypes.Value;

    /// <summary>
    /// Open interface definitions useful when resolving nested generic search input.
    /// </summary>
    internal static IReadOnlyList<Type> OpenInterfaceDefinitions => _openInterfaceDefinitions.Value;


    /// <summary>
    /// Constructs a closed generic type and applies both CLR and ProtoFlux validity rules.
    /// In addition, this explicitly enforces the unmanaged marker that reflection's
    /// MakeGenericType does not enforce by itself.
    /// </summary>
    internal static bool TryConstructGeneric(
        Type definition,
        IReadOnlyList<Type> arguments,
        [NotNullWhen(true)] out Type? constructed)
    {
        constructed = null;

        if (!definition.IsGenericTypeDefinition)
            return false;

        Type[] parameters = definition.GetGenericArguments();
        if (parameters.Length != arguments.Count)
            return false;

        for (int i = 0; i < parameters.Length; i++)
        {
            Type argument = arguments[i];
            if (argument.ContainsGenericParameters ||
                HasUnmanagedConstraint(parameters[i]) && !IsUnmanaged(argument))
            {
                return false;
            }
        }

        try
        {
            Type type = definition.MakeGenericType(arguments.ToArray());
            if (type.ContainsGenericParameters)
                return false;

            PropertyInfo? explicitValidity = type.GetProperty(
                "IsValidGenericType",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

            if ((bool?)explicitValidity?.GetValue(null) == false ||
                !type.IsValidGenericType(validForInstantiation: true))
            {
                return false;
            }

            constructed = type;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidOperationException
                                          or TargetInvocationException
                                          or TypeLoadException)
        {
            return false;
        }
    }


    internal static bool IsReplacementTarget(Type definition)
    {
        if (!IsConcreteGenericDefinition(definition))
            return false;

        if (definition.Namespace == CollectionNodeNamespace)
            return true;

        return definition.Namespace == ProtoFluxNodeNamespacePrefix &&
               _flowNodeNames.Contains(GetUnadornedName(definition));
    }


    internal static bool IsSingleArgumentAppendTarget(Type definition)
    {
        return IsConcreteGenericDefinition(definition) &&
               definition.GetGenericArguments().Length == 1 &&
               definition.FullName is not null &&
               _singleArgumentAppendTargets.Contains(definition.FullName);
    }


    internal static Type[] BuildReplacementCommonTypes(Type definition)
    {
        List<Type> results = [];
        HashSet<Type> seen = GetDefaultGenericInstances(definition);

        if (!TryBuildNonGenericCollectionCandidates(definition, results, seen))
            BuildProjectedCandidates(definition, results, seen);

        return results.ToArray();
    }


    internal static IEnumerable<Type> AppendSingleArgumentCandidates(
        Type definition,
        IEnumerable<Type>? stock)
    {
        if (!IsSingleArgumentAppendTarget(definition))
            return stock ?? Array.Empty<Type>();

        List<Type> results = [];
        HashSet<Type> seen = GetDefaultGenericInstances(definition);

        if (stock is not null)
        {
            foreach (Type type in stock)
            {
                if (seen.Add(type))
                    results.Add(type);
            }
        }

        foreach (Type candidate in CandidateTypes)
        {
            if (TryConstructGeneric(definition, [candidate], out Type? constructed) &&
                seen.Add(constructed))
            {
                results.Add(constructed);
            }
        }

        return results.ToArray();
    }


    private static IReadOnlyList<Type> BuildSeedTypes()
    {
        List<Type> types =
        [
            typeof(string),
            typeof(Uri),
            typeof(object),
            typeof(Type),
            typeof(dummy),
            typeof(DummyEnum),
            typeof(Guid),
            typeof(IWorldElement),
            typeof(IWorldAudioDataSource)
        ];

        foreach (Type textureType in typeof(ITexture).Assembly
                     .GetExportedTypes()
                     .Where(type => type.IsInterface &&
                                    typeof(ITexture).IsAssignableFrom(type) &&
                                    typeof(IAsset).IsAssignableFrom(type))
                     .OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            Type providerType = typeof(IAssetProvider<>).MakeGenericType(textureType);
            if (!types.Contains(providerType))
                types.Add(providerType);
        }

        types.AddRange(_unmanagedSeedTypes);
        types.AddRange(
        [
            typeof(RaycastHit),
            typeof(Slot),
            typeof(User),
            typeof(UserRef),
            typeof(Component),
            typeof(MeshRenderer),
            typeof(Material),
            typeof(IBounded),
            typeof(IField<string>),
            typeof(IField<colorX>)
        ]);

        return Array.AsReadOnly(types.Distinct().ToArray());
    }


    private static IReadOnlyList<Type> BuildCandidateTypes()
    {
        List<Type> types = [.. SeedTypes, typeof(ICollection), typeof(IList)];

        foreach (Type seed in SeedTypes)
        {
            types.Add(typeof(IEnumerable<>).MakeGenericType(seed));
            types.Add(typeof(ICollection<>).MakeGenericType(seed));
            types.Add(typeof(IList<>).MakeGenericType(seed));
            types.Add(typeof(IReadOnlyCollection<>).MakeGenericType(seed));
            types.Add(typeof(IReadOnlyList<>).MakeGenericType(seed));
            types.Add(typeof(IDictionary<,>).MakeGenericType(seed, seed));
        }

        foreach ((Type key, Type value) in _dictionaryProfiles)
            types.Add(typeof(IDictionary<,>).MakeGenericType(key, value));

        return Array.AsReadOnly(types.Distinct().ToArray());
    }


    private static IReadOnlyList<Type> BuildOpenInterfaceDefinitions()
    {
        Type[] types = [.. _genericCarrierDefinitions, typeof(IAssetProvider<>)];
        return Array.AsReadOnly(types);
    }


    private static bool TryBuildNonGenericCollectionCandidates(
        Type definition,
        List<Type> results,
        HashSet<Type> seen)
    {
        Type[] parameters = definition.GetGenericArguments();
        if (parameters.Length != 1)
            return false;

        Type[] constraints = parameters[0].GetGenericParameterConstraints();
        Type[] arguments;

        if (constraints.Contains(typeof(IList)))
            arguments = [typeof(IList)];
        else if (constraints.Contains(typeof(ICollection)))
            arguments = [typeof(ICollection), typeof(IList)];
        else
            return false;

        foreach (Type argument in arguments)
        {
            if (TryConstructGeneric(definition, [argument], out Type? constructed) &&
                seen.Add(constructed))
            {
                results.Add(constructed);
            }
        }

        return true;
    }


    private static void BuildProjectedCandidates(
        Type definition,
        List<Type> results,
        HashSet<Type> seen)
    {
        Type[] parameters = definition.GetGenericArguments();
        Dictionary<Type, Type> carrierConstraints = [];

        foreach (Type parameter in parameters)
        {
            Type? carrierConstraint = parameter.GetGenericParameterConstraints()
                .FirstOrDefault(IsRecognizedGenericCarrier);

            if (carrierConstraint is not null)
                carrierConstraints.Add(parameter, carrierConstraint);
        }

        Type[] freeParameters = parameters
            .Where(parameter => !carrierConstraints.ContainsKey(parameter))
            .ToArray();

        if (freeParameters.Length == 0)
            return;

        bool[] unmanaged = freeParameters.Select(HasUnmanagedConstraint).ToArray();
        IReadOnlyList<Type> representatives = unmanaged.All(value => value)
            ? _unmanagedSeedTypes
            : SeedTypes;

        foreach (Type representative in representatives)
        {
            Dictionary<Type, Type> substitutions = [];
            for (int i = 0; i < freeParameters.Length; i++)
            {
                substitutions.Add(
                    freeParameters[i],
                    unmanaged[i] && !IsUnmanaged(representative) ? typeof(dummy) : representative);
            }

            TryAddProjectedCandidate(definition, parameters, carrierConstraints, substitutions, results, seen);
        }

        if (TryGetDictionaryParameters(
                definition,
                freeParameters,
                carrierConstraints,
                out Type? keyParameter,
                out Type? valueParameter))
        {
            foreach ((Type key, Type value) in _dictionaryProfiles)
            {
                Dictionary<Type, Type> substitutions = new()
                {
                    [keyParameter] = key,
                    [valueParameter] = value
                };

                TryAddProjectedCandidate(definition, parameters, carrierConstraints, substitutions, results, seen);
            }
        }
    }


    private static bool TryGetDictionaryParameters(
        Type definition,
        IReadOnlyList<Type> freeParameters,
        IReadOnlyDictionary<Type, Type> carrierConstraints,
        [NotNullWhen(true)] out Type? keyParameter,
        [NotNullWhen(true)] out Type? valueParameter)
    {
        keyParameter = null;
        valueParameter = null;

        if (freeParameters.Count != 2)
            return false;

        Type? dictionaryConstraint = carrierConstraints.Values.FirstOrDefault(constraint =>
            constraint.IsGenericType &&
            constraint.GetGenericTypeDefinition() == typeof(IDictionary<,>));

        if (dictionaryConstraint is null)
        {
            if (definition.Namespace != CollectionNodeNamespace ||
                !_dictionaryPairNodeNames.Contains(GetUnadornedName(definition)))
            {
                return false;
            }

            keyParameter = freeParameters[0];
            valueParameter = freeParameters[1];
            return true;
        }

        Type[] arguments = dictionaryConstraint.GetGenericArguments();
        if (arguments[0] == arguments[1] ||
            !freeParameters.Contains(arguments[0]) ||
            !freeParameters.Contains(arguments[1]))
            return false;

        keyParameter = arguments[0];
        valueParameter = arguments[1];
        return true;
    }


    private static void TryAddProjectedCandidate(
        Type definition,
        IReadOnlyList<Type> parameters,
        IReadOnlyDictionary<Type, Type> carrierConstraints,
        IReadOnlyDictionary<Type, Type> substitutions,
        List<Type> results,
        HashSet<Type> seen)
    {
        Type[] arguments = new Type[parameters.Count];

        for (int i = 0; i < parameters.Count; i++)
        {
            Type parameter = parameters[i];
            if (substitutions.TryGetValue(parameter, out Type? argument))
            {
                arguments[i] = argument;
                continue;
            }

            if (!carrierConstraints.TryGetValue(parameter, out Type? carrierConstraint) ||
                !TrySubstituteType(carrierConstraint, substitutions, out argument))
            {
                return;
            }

            arguments[i] = argument;
        }

        if (TryConstructGeneric(definition, arguments, out Type? constructed) &&
            seen.Add(constructed))
        {
            results.Add(constructed);
        }
    }


    private static bool TrySubstituteType(
        Type source,
        IReadOnlyDictionary<Type, Type> substitutions,
        [NotNullWhen(true)] out Type? substituted)
    {
        if (source.IsGenericParameter)
            return substitutions.TryGetValue(source, out substituted);

        if (!source.ContainsGenericParameters)
        {
            substituted = source;
            return true;
        }

        if (!source.IsGenericType)
        {
            substituted = null;
            return false;
        }

        Type[] sourceArguments = source.GetGenericArguments();
        Type[] substitutedArguments = new Type[sourceArguments.Length];

        for (int i = 0; i < sourceArguments.Length; i++)
        {
            if (!TrySubstituteType(sourceArguments[i], substitutions, out Type? argument))
            {
                substituted = null;
                return false;
            }

            substitutedArguments[i] = argument;
        }

        try
        {
            substituted = source.GetGenericTypeDefinition().MakeGenericType(substitutedArguments);
            return true;
        }
        catch (ArgumentException)
        {
            substituted = null;
            return false;
        }
    }


    private static bool IsRecognizedGenericCarrier(Type constraint)
    {
        return constraint.IsGenericType &&
               _genericCarrierDefinitions.Contains(constraint.GetGenericTypeDefinition());
    }


    private static bool IsConcreteGenericDefinition(Type definition)
    {
        return definition.IsGenericTypeDefinition && !definition.IsAbstract;
    }


    private static string GetUnadornedName(Type type)
    {
        int tick = type.Name.IndexOf('`');
        return tick < 0 ? type.Name : type.Name[..tick];
    }


    private static HashSet<Type> GetDefaultGenericInstances(Type definition)
    {
        HashSet<Type> instances = [];

        foreach (DefaultGenericInstanceAttribute attribute in
                 definition.GetCustomAttributes<DefaultGenericInstanceAttribute>(inherit: false))
        {
            Type defaultType = attribute.Type;
            if (!defaultType.ContainsGenericParameters &&
                defaultType.IsGenericType &&
                defaultType.GetGenericTypeDefinition() == definition)
            {
                instances.Add(defaultType);
            }
            else if (definition.GetGenericArguments().Length == 1 &&
                     TryConstructGeneric(definition, [defaultType], out Type? constructed))
            {
                instances.Add(constructed);
            }
        }

        return instances;
    }


    private static bool HasUnmanagedConstraint(Type parameter)
    {
        return parameter.IsGenericParameter &&
               parameter.GetCustomAttributesData().Any(attribute =>
                   attribute.AttributeType.FullName == "System.Runtime.CompilerServices.IsUnmanagedAttribute");
    }


    private static bool IsUnmanaged(Type type)
    {
        return IsUnmanaged(type, []);
    }


    private static bool IsUnmanaged(Type type, HashSet<Type> visited)
    {
        if (type.IsPointer || type.IsFunctionPointer || type.IsEnum || type.IsPrimitive)
            return true;

        if (!type.IsValueType || type.ContainsGenericParameters)
            return false;

        Type? nullableElement = Nullable.GetUnderlyingType(type);
        if (nullableElement is not null)
            return IsUnmanaged(nullableElement, visited);

        if (!visited.Add(type))
            return true;

        foreach (FieldInfo field in type.GetFields(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!IsUnmanaged(field.FieldType, visited))
                return false;
        }

        return true;
    }
}


[HarmonyPatch(typeof(WorkerInitializer), nameof(WorkerInitializer.GetCommonGenericTypes))]
internal static class WorkerInitializer_GetCommonGenericTypes_Patch
{
    [HarmonyPrefix]
    private static bool Prefix(Type __0, ref IEnumerable<Type> __result)
    {
        if (CherryPick.Config?.GetValue(CherryPick.Enabled) != true ||
            !GenericTypeCatalog.IsReplacementTarget(__0))
        {
            return true;
        }

        __result = GenericTypeCatalog.BuildReplacementCommonTypes(__0);
        return false;
    }


    [HarmonyPostfix]
    private static void Postfix(Type __0, ref IEnumerable<Type> __result)
    {
        if (CherryPick.Config?.GetValue(CherryPick.Enabled) != true ||
            GenericTypeCatalog.IsReplacementTarget(__0) ||
            !GenericTypeCatalog.IsSingleArgumentAppendTarget(__0))
        {
            return;
        }

        __result = GenericTypeCatalog.AppendSingleArgumentCandidates(__0, __result);
    }
}
