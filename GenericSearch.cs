using System.Reflection;

using Elements.Core;

using FrooxEngine;

namespace CherryPick;


/// <summary>
/// Engine-state-independent parsing, type resolution, and carrier inference for generic search.
/// </summary>
internal static class GenericSearch
{
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
    private static readonly Type[] _recognizedCarrierDefinitions =
    [
        typeof(IEnumerable<>),
        typeof(ICollection<>),
        typeof(IList<>),
        typeof(IReadOnlyCollection<>),
        typeof(IReadOnlyList<>),
        typeof(IDictionary<,>)
    ];


    internal static IReadOnlyList<string>? ParseGenericQuery(string query, out string matchText)
    {
        int genericStart = -1;
        int squareDepth = 0;
        for (int i = 0; i < query.Length; i++)
        {
            switch (query[i])
            {
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth == 0)
                    {
                        matchText = query;
                        return null;
                    }
                    squareDepth--;
                    break;
                case '<' when squareDepth == 0:
                    genericStart = i;
                    i = query.Length;
                    break;
            }
        }

        if (genericStart <= 0)
        {
            matchText = query;
            return null;
        }

        matchText = query[..genericStart].TrimEnd();
        List<string> arguments = [];
        int angleDepth = 1;
        squareDepth = 0;
        int argumentStart = genericStart + 1;
        bool outerClosed = false;

        for (int i = argumentStart; i < query.Length; i++)
        {
            char character = query[i];
            switch (character)
            {
                case '<' when squareDepth == 0:
                    angleDepth++;
                    break;
                case '>' when squareDepth == 0:
                    angleDepth--;
                    if (angleDepth < 0)
                        return null;

                    if (angleDepth == 0)
                    {
                        if (squareDepth != 0 || query[(i + 1)..].Any(c => !char.IsWhiteSpace(c)))
                            return null;

                        arguments.Add(query[argumentStart..i]);
                        outerClosed = true;
                        i = query.Length;
                    }
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth == 0)
                        return null;
                    squareDepth--;
                    break;
                case ',' when angleDepth == 1 && squareDepth == 0:
                    arguments.Add(query[argumentStart..i]);
                    argumentStart = i + 1;
                    break;
            }
        }

        if (!outerClosed)
        {
            if (angleDepth <= 0 || squareDepth != 0)
                return null;
            arguments.Add(query[argumentStart..]);
        }

        return arguments.Count > 0 && arguments.All(argument => !string.IsNullOrWhiteSpace(argument))
            ? arguments
            : null;
    }


    internal static Type? ResolveGenericArgument(string genericType, Func<string, Type?>? exactResolver = null)
    {
        if (!TryCompleteGenericTypeExpression(genericType, out string completed))
            return null;

        if (exactResolver is not null)
        {
            try
            {
                Type? exact = exactResolver(completed);
                if (IsClosedGenericArgument(exact))
                    return exact;
            }
            catch (Exception)
            {
                // Fall through to the local fuzzy resolver.
            }
        }

        try
        {
            Type? fuzzy = NiceTypeParser.TryParse(completed, ResolveFuzzyTypeName);
            return IsClosedGenericArgument(fuzzy) ? fuzzy : null;
        }
        catch (Exception)
        {
            return null;
        }
    }


    internal static bool TryInferLeadingCarrier(Type definition, IReadOnlyList<Type> suppliedArguments, out Type? constructed)
    {
        constructed = null;
        if (!TryGetLeadingCarrierConstraint(definition, out Type? carrierConstraint) || carrierConstraint is null)
            return false;

        Type[] parameters = definition.GetGenericArguments();

        // A closed carrier is unwrapped only when it is exactly the declared interface.
        // Concrete implementations are intentionally not searched for compatible interfaces.
        if (suppliedArguments.Count == 1)
        {
            Dictionary<Type, Type> bindings = [];
            HashSet<Type> freeParameters = [.. parameters.Skip(1)];
            if (TryUnifyExactCarrier(carrierConstraint, suppliedArguments[0], freeParameters, bindings) &&
                freeParameters.All(bindings.ContainsKey))
            {
                Type[] inferredArguments = new Type[parameters.Length];
                inferredArguments[0] = suppliedArguments[0];
                for (int i = 1; i < parameters.Length; i++)
                    inferredArguments[i] = bindings[parameters[i]];

                if (GenericTypeCatalog.TryConstructGeneric(definition, inferredArguments, out constructed))
                    return true;
            }
        }

        // Otherwise the supplied values are the free generic arguments and the
        // leading carrier is projected directly from its declared constraint.
        if (suppliedArguments.Count != parameters.Length - 1)
            return false;

        Dictionary<Type, Type> substitutions = [];
        for (int i = 1; i < parameters.Length; i++)
            substitutions.Add(parameters[i], suppliedArguments[i - 1]);

        Type? carrier = SubstituteGenericParameters(carrierConstraint, substitutions);
        if (carrier is null || !IsClosedGenericArgument(carrier))
            return false;

        Type[] completedArguments = new Type[parameters.Length];
        completedArguments[0] = carrier;
        for (int i = 1; i < parameters.Length; i++)
            completedArguments[i] = suppliedArguments[i - 1];

        return GenericTypeCatalog.TryConstructGeneric(definition, completedArguments, out constructed);
    }


    private static bool TryCompleteGenericTypeExpression(string expression, out string completed)
    {
        completed = expression.Trim();
        if (completed.Length == 0)
            return false;

        int angleDepth = 0;
        int squareDepth = 0;
        foreach (char character in completed)
        {
            switch (character)
            {
                case '<' when squareDepth == 0:
                    angleDepth++;
                    break;
                case '>' when squareDepth == 0:
                    angleDepth--;
                    if (angleDepth < 0)
                        return false;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth == 0)
                        return false;
                    squareDepth--;
                    break;
            }
        }

        if (squareDepth != 0)
            return false;

        if (angleDepth > 0)
            completed += new string('>', angleDepth);
        return true;
    }


    private static bool IsClosedGenericArgument(Type? type)
    {
        return type is not null &&
            !type.IsGenericParameter &&
            !type.IsGenericTypeDefinition &&
            !type.ContainsGenericParameters;
    }


    private static Type? ResolveFuzzyTypeName(string query)
    {
        string trimmed = query.Trim();
        if (trimmed.Length == 0)
            return null;

        bool requestsOpenGeneric = TryGetRequestedGenericArity(trimmed, out int genericArity);
        if (!requestsOpenGeneric)
        {
            Type? alias = FindKnownGenericArgumentAlias(trimmed);
            if (alias is not null)
                return alias;
        }

        IEnumerable<Type> candidates = requestsOpenGeneric
            ? GenericTypeCatalog.OpenInterfaceDefinitions.Where(type => type.GetGenericArguments().Length == genericArity)
            : GenericTypeCatalog.CandidateTypes
                .Concat(_genericArgumentTypes.Value)
                .Where(type => !type.IsGenericTypeDefinition);
        string matchQuery = requestsOpenGeneric ? trimmed[..trimmed.LastIndexOf('`')] : trimmed;

        return candidates
            .Distinct()
            .Select(type => new
            {
                Type = type,
                Rank = requestsOpenGeneric
                    ? GetOpenGenericDefinitionMatchRank(type, matchQuery)
                    : GetGenericArgumentMatchRank(type, matchQuery)
            })
            .Where(match => match.Rank >= 0)
            .OrderBy(match => match.Rank)
            .ThenBy(match => match.Type.GetNiceName().Length)
            .ThenBy(match => match.Type.GetNiceName(), StringComparer.OrdinalIgnoreCase)
            .Select(match => match.Type)
            .FirstOrDefault();
    }


    private static bool TryGetRequestedGenericArity(string query, out int arity)
    {
        arity = 0;
        int separator = query.LastIndexOf('`');
        return separator >= 0 &&
            separator < query.Length - 1 &&
            int.TryParse(query[(separator + 1)..], out arity) &&
            arity > 0;
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


    private static bool TryGetLeadingCarrierConstraint(Type definition, out Type? carrierConstraint)
    {
        carrierConstraint = null;
        if (!definition.IsGenericTypeDefinition)
            return false;

        Type[] parameters = definition.GetGenericArguments();
        if (parameters.Length < 2)
            return false;

        Type[] recognizedConstraints = parameters[0]
            .GetGenericParameterConstraints()
            .Where(constraint => constraint.IsGenericType &&
                _recognizedCarrierDefinitions.Contains(constraint.GetGenericTypeDefinition()))
            .ToArray();
        if (recognizedConstraints.Length != 1)
            return false;

        Type constraint = recognizedConstraints[0];
        HashSet<Type> referencedParameters = [];
        CollectGenericParameters(constraint, referencedParameters);
        if (!referencedParameters.SetEquals(parameters.Skip(1)))
            return false;

        carrierConstraint = constraint;
        return true;
    }


    private static void CollectGenericParameters(Type type, HashSet<Type> parameters)
    {
        if (type.IsGenericParameter)
        {
            parameters.Add(type);
            return;
        }

        if (type.HasElementType)
        {
            Type? elementType = type.GetElementType();
            if (elementType is not null)
                CollectGenericParameters(elementType, parameters);
        }

        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
                CollectGenericParameters(argument, parameters);
        }
    }


    private static bool TryUnifyExactCarrier(Type formal, Type actual, HashSet<Type> freeParameters, Dictionary<Type, Type> bindings)
    {
        if (formal.IsGenericParameter)
        {
            if (!freeParameters.Contains(formal))
                return false;

            if (bindings.TryGetValue(formal, out Type? bound))
                return bound == actual;

            bindings.Add(formal, actual);
            return true;
        }

        if (formal.IsArray)
        {
            return actual.IsArray &&
                formal.GetArrayRank() == actual.GetArrayRank() &&
                TryUnifyExactCarrier(formal.GetElementType()!, actual.GetElementType()!, freeParameters, bindings);
        }

        if (!formal.IsGenericType)
            return formal == actual;

        if (!actual.IsGenericType || formal.GetGenericTypeDefinition() != actual.GetGenericTypeDefinition())
            return false;

        Type[] formalArguments = formal.GetGenericArguments();
        Type[] actualArguments = actual.GetGenericArguments();
        for (int i = 0; i < formalArguments.Length; i++)
        {
            if (!TryUnifyExactCarrier(formalArguments[i], actualArguments[i], freeParameters, bindings))
                return false;
        }

        return true;
    }


    private static Type? SubstituteGenericParameters(Type type, IReadOnlyDictionary<Type, Type> substitutions)
    {
        if (type.IsGenericParameter)
            return substitutions.TryGetValue(type, out Type? substitution) ? substitution : null;

        if (type.IsArray)
        {
            Type? elementType = SubstituteGenericParameters(type.GetElementType()!, substitutions);
            if (elementType is null)
                return null;
            return type.GetArrayRank() == 1 ? elementType.MakeArrayType() : elementType.MakeArrayType(type.GetArrayRank());
        }

        if (!type.IsGenericType)
            return type;

        Type[] sourceArguments = type.GetGenericArguments();
        Type[] substitutedArguments = new Type[sourceArguments.Length];
        for (int i = 0; i < sourceArguments.Length; i++)
        {
            Type? substituted = SubstituteGenericParameters(sourceArguments[i], substitutions);
            if (substituted is null)
                return null;
            substitutedArguments[i] = substituted;
        }

        try
        {
            return type.GetGenericTypeDefinition().MakeGenericType(substitutedArguments);
        }
        catch (ArgumentException)
        {
            return null;
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


    private static int GetOpenGenericDefinitionMatchRank(Type type, string query)
    {
        string niceName = type.GetNiceName();
        int genericStart = niceName.IndexOf('<');
        if (genericStart >= 0)
            niceName = niceName[..genericStart];

        string name = type.Name;
        int arityStart = name.LastIndexOf('`');
        if (arityStart >= 0)
            name = name[..arityStart];

        string? fullName = type.FullName;
        arityStart = fullName?.LastIndexOf('`') ?? -1;
        if (arityStart >= 0)
            fullName = fullName![..arityStart];

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
}
