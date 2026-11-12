namespace ScriptLangCore;

// ============================================================================
// Extension members (C# 14 style): registration, static dispatch, instance dispatch
//
//   public static class MyExtensions
//   {
//       extension<T>(List<T>)
//       {
//           public static List<T> Create() => [];
//       }
//   }
//
// The extended type is named by a TypeInfo (a name plus its type arguments), never by a CLR Type, so
// matching is by NAME and ARITY and the call site supplies the arguments (`List<int>.Create()`).
// A block may also be written at top level, where the container class is simply absent.
//
// A `public static` member is reached through the TYPE (`List<int>.Create()`). A member without
// `static` is reached through a VALUE of the extended type (`value.MyMember()`), and only when
// nothing the value OWNS answers: instance members take priority over extensions (C# spec 7.6.5.2),
// the same rule the CLR-extension scan in method dispatch already applies.
// ============================================================================

internal sealed record ExtensionMemberSet(
    string TargetName,
    int TargetArity,
    TypeInfo Target,
    IReadOnlyList<string> TypeParameters,
    IReadOnlyList<FuncDecl> Members,
    string? ContainerName,
    string? ReceiverName,
    IReadOnlyList<ExtensionPropertyDecl> Properties,
    IReadOnlyList<FuncDecl> Operators)
{
    /// <summary>The target as the block wrote it (`List<T>`), for messages.</summary>
    public string TargetDisplay => TargetArity == 0
        ? TargetName
        : TargetName + "<" + string.Join(", ", TypeParameters) + ">";
}

internal partial class Interpreter
{
    private readonly List<ExtensionMemberSet> _extensionSets = [];

    /// <summary>The same sets, indexed TARGET NAME → MEMBER NAME → the members declared under it, so a member
    /// lookup does not walk the blocks at all. Measured before adding it: with 200 blocks the caught-MISS path
    /// was 236-311 ms per 10,000 misses against 157-213 ms with one block, while the HIT path showed no trend
    /// (the invoke machinery dominates it). A name-only index was tried FIRST and did not move that number:
    /// every one of those 200 blocks targeted the same type, so the walk was over the members OF the receiver's
    /// target, which is why the key carries the member name as well. Registration order is preserved inside
    /// each list, which is what keeps first-match-wins identical to the scan it replaces.
    /// Created on first registration, because the comparer it must use is set by the constructor.</summary>
    private Dictionary<string, Dictionary<string, List<(ExtensionMemberSet Set, FuncDecl Member)>>>? _setsByTargetName;

    private Dictionary<string, Dictionary<string, List<(ExtensionMemberSet Set, FuncDecl Member)>>> SetsByName
        => _setsByTargetName ??= new Dictionary<string, Dictionary<string, List<(ExtensionMemberSet Set, FuncDecl Member)>>>(_comparer);

    /// <summary>The same index for PROPERTIES, keyed by target name then property name. A property and a method
    /// of one name are two different members (in C# too), so they are indexed separately and cannot collide.</summary>
    private Dictionary<string, Dictionary<string, List<(ExtensionMemberSet Set, ExtensionPropertyDecl Property)>>>? _propsByTargetName;

    private Dictionary<string, Dictionary<string, List<(ExtensionMemberSet Set, ExtensionPropertyDecl Property)>>> PropsByName
        => _propsByTargetName ??= new Dictionary<string, Dictionary<string, List<(ExtensionMemberSet Set, ExtensionPropertyDecl Property)>>>(_comparer);

    /// <summary>Adds a set to the ordered list and to the member and property indexes, the only place any of the
    /// three is written.</summary>
    private void AddExtensionSet(ExtensionMemberSet set)
    {
        _extensionSets.Add(set);
        var byName = SetsByName;
        if (!byName.TryGetValue(set.TargetName, out var byMember))
            byName[set.TargetName] = byMember = new Dictionary<string, List<(ExtensionMemberSet, FuncDecl)>>(_comparer);
        foreach (var member in set.Members)
        {
            if (!byMember.TryGetValue(member.Name, out var list))
                byMember[member.Name] = list = [];
            list.Add((set, member));
        }

        var propsByName = PropsByName;
        if (!propsByName.TryGetValue(set.TargetName, out var byProp))
            propsByName[set.TargetName] = byProp = new Dictionary<string, List<(ExtensionMemberSet, ExtensionPropertyDecl)>>(_comparer);
        foreach (var property in set.Properties)
        {
            if (!byProp.TryGetValue(property.Name, out var list))
                byProp[property.Name] = list = [];
            list.Add((set, property));
        }

        var opsByName = OpsByName;
        if (!opsByName.TryGetValue(set.TargetName, out var byOp))
            opsByName[set.TargetName] = byOp = new Dictionary<TokenType, List<ExtensionMemberSet>>();
        foreach (var declared in set.Operators)
        {
            if (declared.OperatorToken is not { } opToken) continue;
            if (!byOp.TryGetValue(opToken, out var opList))
                byOp[opToken] = opList = [];
            opList.Add(set);
        }
    }

    /// <summary>The same index for OPERATORS, keyed by target name then operator token: an operator has no name,
    /// and the lookup arrives with the token it is asked to apply.</summary>
    private Dictionary<string, Dictionary<TokenType, List<ExtensionMemberSet>>>? _opsByTargetName;

    private Dictionary<string, Dictionary<TokenType, List<ExtensionMemberSet>>> OpsByName
        => _opsByTargetName ??= new Dictionary<string, Dictionary<TokenType, List<ExtensionMemberSet>>>(_comparer);

    /// <summary>The blocks that declare this operator for a target name. A block declares one operator per token,
    /// which the registration enforces, so the arity is what tells two of them apart.</summary>
    private List<ExtensionMemberSet> OperatorsNamed(string targetName, TokenType op)
        => _opsByTargetName is { } byName
           && byName.TryGetValue(targetName, out var byOp)
           && byOp.TryGetValue(op, out var list) ? list : [];
    /// <summary>The properties declared for a target name and property name, in declaration order (both halves,
    /// the caller filters); empty when none.</summary>
    private List<(ExtensionMemberSet Set, ExtensionPropertyDecl Property)> PropertiesNamed(string targetName, string propertyName)
        => _propsByTargetName is { } byName
           && byName.TryGetValue(targetName, out var byProp)
           && byProp.TryGetValue(propertyName, out var list) ? list : [];

    /// <summary>The members declared for a target name and member name, in declaration order (both static and
    /// instance ones, the caller filters, exactly as the scan did); empty when none.</summary>
    private List<(ExtensionMemberSet Set, FuncDecl Member)> MembersNamed(string targetName, string memberName)
        => _setsByTargetName is { } byName
           && byName.TryGetValue(targetName, out var byMember)
           && byMember.TryGetValue(memberName, out var list) ? list : [];

    /// <summary>Forgets every registered `extension` block. A re-run of a FILE is a fresh declaration unit even
    /// when the surrounding state is kept: `.looprun` re-reads and re-parses the source every iteration, so the
    /// file's blocks arrive again by design, and without this the duplicate guard fires on the script's OWN
    /// declaration. That error is caught as a script error and printed nowhere, so the iteration executed nothing
    /// while the tool still reported a rate for it, measured with file markers written by the script itself, 1 of
    /// 3 iterations ran. `Reset(clearTypes: true)` also clears these, which is why `.gc leak` needed no caller
    /// change.</summary>
    internal void ClearExtensionSets()
    {
        _extensionSets.Clear();
        _extensionLookup.Clear();
        _setsByTargetName?.Clear();
        _propsByTargetName?.Clear();
        _opsByTargetName?.Clear();
        _extensionTargetNames.Clear();
    }

    /// <summary>The names `is`/`as` accept as a GUARD rather than as a type. A block over one is refused: the
    /// guard resolves no receiver, so the block would register and never fire (measured for `Iterable`, `Class`
    /// and `Struct`, `[1,2].FromIterable()` answered "Method 'FromIterable' not found on type int[]").</summary>
    internal static readonly string[] ContractTargetNames = ["Iterable", "RandomAccess", "Function", "Class", "Struct"];

    private bool IsContractTargetName(string name)
        => ContractTargetNames.Any(c => c.Equals(name, Comparison));

    /// <summary>True when the name is a DECLARED interface. A block over one is refused for the same reason a
    /// contract name is: a value never reports an interface as its type, and the base-class walk matches class
    /// names, so the block would register and never fire. Matching a value against the interfaces it implements
    /// is design C, the step after this one.</summary>

    /// <summary>True when the target NAME takes type arguments, so an arity-0 advice may spell `Name&lt;T…&gt;`:
    /// a script generic class or struct, one of the collection names the language registers, or a CLR generic.
    /// This exists so the advice is COMPLIABLE in both directions, `extension&lt;T&gt;(List)` written bare really
    /// wants `List&lt;T&gt;`, while `extension&lt;T&gt;(string)` must not be told to write `string&lt;T&gt;`, which
    /// cannot be written (measured: that was the advice before this check).</summary>
    private bool TargetTakesTypeArguments(string name)
    {
        if (name is "List" or "Dictionary" or "HashSet" or "Queue" or "Stack") return true;
        if (_genericClassTemplates.ContainsKey(name)) return true;
        return ResolveTypeToClr(name, lax: !_caseSensitive) is { IsGenericTypeDefinition: true };
    }

    /// <summary>Registers one `extension` block. Called for a top-level block and for every block inside a
    /// class body, so both declaration sites share this one path.</summary>
    private void RegisterExtensionMembers(IReadOnlyList<string> typeParameters, TypeInfo target,
        IReadOnlyList<FuncDecl> members, string? containerName, string? receiverName = null,
        IReadOnlyList<ExtensionPropertyDecl>? properties = null, IReadOnlyList<FuncDecl>? operators = null)
    {
        ClearExtensionLookupCache();   // a new block can answer a name a cached miss refused
        properties ??= [];
        operators ??= [];
        if (members.Count == 0 && properties.Count == 0 && operators.Count == 0) return;
        var name = target.TypeName ?? TypeInfo.SimpleTypeName(target.BaseType);
        // Remember every name a block was written over, so the target walk can ask whether one of them is
        // an INTERFACE: only those are ever asked, which keeps a script with no block free of the cost.
        // EVERY target is remembered, not only one that is already a declared interface: a block may be written
        // BEFORE the interface it names, and the question ("is this a declared interface, and does the value
        // match it?") is asked at LOOKUP time, against the registry as it stands then.
        if (!_extensionTargetNames.Contains(name, _comparer))
            _extensionTargetNames.Add(name);
        var arity = target.GenericArgs?.Count ?? 0;

        // An ARRAY is a SHAPE, not a NAME, so it cannot be a target, and the arity check below cannot see it,
        // because that check only reads the bare name:
        //   * `extension<T>(T[])` reported "the extended type takes 0 type parameter(s)" and advised the
        //     bare-type-parameter recipe, a true rule, but NOT the reason this block is refused;
        //   * `extension(string[])` was not refused at all: it registered silently and then matched no
        //     receiver, so every call answered "Method 'X' not found on type string[]" while the block sat
        //     there looking accepted (both measured). Refused with the shape the author wrote, and with a
        //     recipe that works: no spelling adds a member to an array from outside it.
        if (target.ArrayRank > 0 || target.JaggedDepth > 0)
        {
            var shape = name
                + string.Concat(Enumerable.Repeat("[]", target.JaggedDepth))
                + (target.ArrayRank > 0 ? "[" + new string(',', target.ArrayRank - 1) + "]" : "");
            throw new ScriptException(
                $"An extension target cannot be an array: '{shape}' is a shape, not a name. No block can add a "
                + "member to an array from outside it, write a plain function that takes the array and call it as "
                + "`Fn(arr)`; extending an element type adds members to the array's ITEMS, never to the array.");
        }

        // A CONTRACT name is not a type either, and a block over one is DEAD in the same silent way the array
        // target was: `extension(Iterable)`, `extension(Class)` and `extension(Struct)` all registered without a
        // word and then matched no receiver (measured: `[1,2].FromIterable()` answered "Method 'FromIterable' not
        // found on type int[]"). They name a GUARD, what `is`/`as` accepts, not a type anything can be named by,
        // so the block is refused with the rule and the recipe that works.
        // A block over a DECLARED INTERFACE is meaningful now: `is` reads a contract as a shape as well as a
        // promise, so such a block speaks for every value that MATCHES the interface (see TargetCandidates). A
        // STATIC member cannot work that way, because a static member is reached through a TYPE name and an
        // interface is not a type a value reports, so that one shape is refused rather than left dead.
        if (IsDeclaredInterfaceName(name) && members.Any(m => m.IsStatic))
            throw new ScriptException(
                $"'{name}' is an INTERFACE, and a static member is reached through a TYPE name: a value never "
                + "reports an interface, so that member could never answer. Write it as an INSTANCE member, which "
                + $"a block over '{name}' provides to every value that matches the contract.");
        if (IsContractTargetName(name))
            throw new ScriptException(
                $"An extension target cannot be a CONTRACT name: '{name}' is a guard for `is`/`as`, not a type, so "
                + "a block over it would never fire. Declare one block per concrete type "
                + "(`extension(string)`, `extension<T>(List<T>)`), or ask the value with `is`.");

        if (arity != typeParameters.Count)
        {
            // The advice has to be COMPLIABLE, spelled with names that can actually be written. Three shapes were
            // not, all found by measuring the message rather than by reading it:
            //   * `extension<T>(T)` (a target that IS the block's own type parameter) used to advise
            //     `extension<T>(T<T>)`, which cannot be written at all, an extension target must be a NAMED type;
            //   * `extension(Dictionary<string, int>)` (a CONCRETE instantiation) advised `extension<>(Dictionary<>)`
            //    , an empty type-parameter list, while the fix is to extend the type GENERICALLY;
            //   * a target that takes NO type argument (say `extension<T>(string)`) advised
            //     `extension<T>(string<T>)`, which cannot be written either: the arity-0 branch may only offer
            //     `Name<T…>` when the NAME actually takes type arguments (`List` written bare), so the generic case
            //     and the non-generic one get one sentence each.
            var bareTypeParameter = typeParameters.Any(p => p.Equals(name, Comparison));
            var names = string.Join(", ", Enumerable.Range(1, arity).Select(i => "T" + i));
            var written = string.Join(", ", typeParameters);
            var advice = bareTypeParameter
                ? "An extension block cannot extend its OWN type parameter: the target must be a NAMED type, so a "
                  + "block cannot add a member to every kind of value. Declare one block per type you care about, "
                  + "or put the member on the type itself."
                : arity == 0
                    ? TargetTakesTypeArguments(name)
                        ? $"Write extension<{written}>({name}<{written}>) when the extended type takes a type argument, "
                          + "or drop the block's type parameters when it takes none."
                        : $"This target takes no type argument, so drop the block's type parameters: "
                          + $"extension({name}) is the block you want."
                    : $"Write extension<{names}>({name}<{names}>) so the block's type parameters match the type's.";
            throw new ScriptException(
                $"Extension block for '{target}' declares {typeParameters.Count} type parameter(s) but the extended "
                + $"type takes {arity}. {advice}");
        }

        foreach (var member in members)
        {
            RefuseStaticReceiverUse(member.Name, "member", member.IsStatic, member.Body, receiverName);

            FuncDecl? clash = null;
            foreach (var (set, declared) in MembersNamed(name, member.Name))
            {
                if (set.TargetArity != arity || declared.IsStatic != member.IsStatic) continue;
                clash = declared;
                break;
            }
            if (clash != null)
                throw new ScriptException(
                    $"Extension member '{member.Name}' for '{name}' ({(member.IsStatic ? "static" : "instance")}) "
                    + "is already declared.");
        }

        foreach (var property in properties)
        {
            // The same rule the members are held to, applied to the accessors' bodies: a STATIC property has no
            // receiver, so an accessor reading `this` (or the block's receiver name) is refused at declaration.
            RefuseStaticReceiverUse(property.Name, "property", property.IsStatic, property.Getter?.Body, receiverName);
            RefuseStaticReceiverUse(property.Name, "property", property.IsStatic, property.Setter?.Body, receiverName);

            var propertyClash = false;
            foreach (var (set, declared) in PropertiesNamed(name, property.Name))
            {
                if (set.TargetArity != arity || declared.IsStatic != property.IsStatic) continue;
                propertyClash = true;
                break;
            }
            if (propertyClash)
                throw new ScriptException(
                    $"Extension property '{property.Name}' for '{name}' "
                    + $"({(property.IsStatic ? "static" : "instance")}) is already declared.");
        }

        foreach (var declared in operators)
        {
            // An operator is static by construction, because the parser refuses anything else. C# makes an
            // extension operator findable by requiring one of its OPERANDS to be the extended type: without that
            // rule a block would claim an operator for two foreign types and no lookup could justify applying it.
            if (!declared.Params.Any(p => ParameterIsTarget(p.Type, name, arity)))
            {
                // A reader who wrote the type BARE (`Vec` where the target is `Vec<T>`) is told which spelling was
                // missing, or the rule reads as though their operand were a foreign type when it is the extended
                // type minus its type argument, which is what a script writes next.
                var written = typeParameters.Count == 0
                    ? name
                    : name + "<" + string.Join(", ", typeParameters) + ">";
                var bare = declared.Params.FirstOrDefault(p =>
                    p.Type.TypeName is { } writtenName && writtenName.Equals(name, Comparison)
                    && (p.Type.GenericArgs?.Count ?? 0) != arity);
                throw new ScriptException(bare.Type is { } bareType
                    ? $"Extension operator '{declared.OperatorToken}' takes '{bareType.TypeName}' as an operand, which "
                      + $"is the target without its type argument(s). Write '{written}'."
                    : $"Extension operator '{declared.OperatorToken}' for '{name}' must take the extended type as one "
                      + "of its operands: an operator whose operands are both foreign types is never the one a block "
                      + "can apply.");
            }

            foreach (var existing in OperatorsNamed(name, declared.OperatorToken!.Value))
                if (existing.TargetArity == arity)
                    throw new ScriptException(
                        $"Extension operator '{declared.OperatorToken}' for '{name}' is already declared.");
        }

        // The two lists are OPTIONAL at this entry point (callers that declare neither omit them) and
        // NON-NULL inside: one normalization here is why the set can declare them required, and why no
        // read of them needs a null check.
        properties ??= [];
        operators ??= [];

        // A member of a generic block is generic over the BLOCK's type parameters (C# 14 scopes them over
        // the block's members), so hand each member those parameters: the engine's existing generic
        // instantiation then BINDS them from the call site and substitutes the signature with them.
        var bound = typeParameters.Count == 0
            ? members
            : members.Select(m => WithBlockTypeParameters(m, typeParameters)).ToList();
        var boundProperties = typeParameters.Count == 0
            ? properties
            : properties.Select(p => WithBlockTypeParameters(p, typeParameters)).ToList();
        var boundOperators = typeParameters.Count == 0
            ? operators
            : operators.Select(o => WithBlockTypeParameters(o, typeParameters)).ToList();

        // The VM runs a body only when it HAS bytecode, and a member declared in a unit the compiler never
        // compiles has none, which is why these bodies ran on the tree while their callers did not. Compile
        // them here, the same way a local function is compiled before it is called.
        //
        // The engine decides which bodies it can take, and it declines two kinds on purpose: a member with
        // type parameters (instantiation is interpreter work, a concrete clone's Compiled is null by
        // construction), and an INSTANCE member's body, because its `this` has no receiver slot on the VM
        // path (the method path binds the receiver as a trailing local, and an extension member has none).
        // So a static member of a non-generic block ends up on the VM, and the rest stay interpreted.
        // An accessor is a member here: a getter takes no parameters and a setter one, so both reach the VM's
        // flat path exactly as a member of the same shape does.
        if (typeParameters.Count == 0)
        {
            foreach (var member in bound)
                if (member.Compiled == null) member.Compiled = CompileExtensionMemberBody(member, receiverName);
            foreach (var property in boundProperties)
            {
                if (property.Getter is { Compiled: null } getter)
                    getter.Compiled = CompileExtensionMemberBody(getter, receiverName);
                if (property.Setter is { Compiled: null } setter)
                    setter.Compiled = CompileExtensionMemberBody(setter, receiverName);
            }
            foreach (var declared in boundOperators)
                if (declared.Compiled == null) declared.Compiled = CompileExtensionMemberBody(declared, receiverName);
        }

        AddExtensionSet(new ExtensionMemberSet(name, arity, target, typeParameters, bound, containerName, receiverName,
            boundProperties, boundOperators));
    }

    /// <summary>Refuses a `static` member or property whose body reaches for a receiver it does not have, `this`,
    /// or the name a block gave its receiver. A static member reading `this` used to read an EMPTY value silently:
    /// `public static string S() => "[" + this + "]"` answered "[]" at `string.S()`.</summary>
    private void RefuseStaticReceiverUse(string name, string kind, bool isStatic, IReadOnlyList<Stmt>? body,
        string? receiverName)
    {
        if (!isStatic || body == null) return;
        var usesThis = BodyReferencesName(body, "this");
        var usesReceiverName = receiverName != null && BodyReferencesName(body, receiverName);
        if (!usesThis && !usesReceiverName) return;
        throw new ScriptException(
            $"Extension {kind} '{name}' is `static`, so it has no receiver: "
            + (usesThis ? "`this`" : $"`{receiverName}`")
            + " is empty there. Drop `static` to reach the receiver's own members, or take the value as a "
            + "parameter.");
    }

    /// <summary>A copy of a property whose accessors are generic over the enclosing block's type parameters.</summary>
    private static ExtensionPropertyDecl WithBlockTypeParameters(ExtensionPropertyDecl property,
        IReadOnlyList<string> blockTypeParameters)
        => ExtensionPropertyDecl.Create(property.Name, property.Type, property.Access, property.IsStatic,
            property.Getter == null ? null : WithBlockTypeParameters(property.Getter, blockTypeParameters),
            property.Setter == null ? null : WithBlockTypeParameters(property.Setter, blockTypeParameters),
            property.Line, property.Column);

    /// <summary>Compiles an extension member's body for the VM. A static member takes the ordinary function
    /// path. An INSTANCE member is compiled with a METHOD's context, because its body speaks `this`, the plain
    /// function path has no receiver context, which is what left these bodies on the tree after every other
    /// extension path had reached the VM. The receiver arrives at invocation as the trailing `this` local, the
    /// layout the method path already uses ([params..., this]).
    /// A block that NAMED its receiver hands the compiler that alias, so the name compiles to the same slot as
    /// `this` instead of being read as a global.
    /// A body the FLAT path cannot take gets no bytecode at all: the tree invocation is the only one left for it,
    /// and the tree cannot supply the receiver, measured, a member declaring `ref`/`out` (the shape that skips the
    /// flat path) reported "Cannot access property 'Length' on null" from its own `this` when it ran compiled.</summary>
    private Bytecode? CompileExtensionMemberBody(FuncDecl member, string? receiverName = null)
    {
        if (member.IsCoroutine) return null;
        if (DeclaresRefOrOut(member)) return null;
        // A BARE-NAME WRITE is the third shape the flat path cannot take, for the reason the class funnels state: the
        // compiler cannot tell a global the file declares from an instance field, and the TREE resolves it live. An
        // extension member had no such guard, so `extension(string s3) { public int Stored13 { set { seen13 = value; } } }`
        // with `var seen13 = 0;` declared AFTER the block compiled the setter and the assignment answered
        // "Undefined variable 'seen13'" (crash_extension_generics 13d). No instance fields can be in scope for a bare
        // name here either: a receiver member is written `this.X` or through the block's receiver name.
        if (Compiler.HasAmbiguousBareNameWrite(member, null, CaseSensitive)) return null;
        return CompileEngineStyle(member, isMethod: !member.IsStatic, receiverAlias: receiverName);
    }

    /// <summary>True when a member declares a `ref` or `out` parameter, in any position, the one shape that
    /// cannot take the flat invocation path, because its arguments are the CALLER's variables.</summary>
    internal static bool DeclaresRefOrOut(FuncDecl member)
        => (member.IsRefParams != null && member.IsRefParams.Any(b => b))
           || (member.IsOutParams != null && member.IsOutParams.Any(b => b));

    /// <summary>A copy of a member that is generic over the enclosing block's type parameters, with any
    /// type parameters of its own appended.</summary>
    private static FuncDecl WithBlockTypeParameters(FuncDecl member, IReadOnlyList<string> blockTypeParameters)
    {
        var copy = FuncDecl.Create(member.Name, member.Params, member.IsRefParams, member.IsParams, member.Body,
            member.ReturnType, member.OperatorToken, member.Access, member.Line, member.Column,
            [.. blockTypeParameters, .. member.TypeParameters], member.IsOutParams);
        // `IsStatic` and `IsCoroutine` are not Create parameters, the parser sets them on the member after
        // construction, so the copy has to carry them, or a static member would be registered as instance.
        copy.IsStatic = member.IsStatic;
        copy.IsCoroutine = member.IsCoroutine;
        return copy;
    }

    /// <summary>The receiver's own type arguments, in the order a target writes them: a dictionary gives its key
    /// and value types, a list, set or array its element type, and a generic class instance the arguments it was
    /// instantiated with. Null when it reports none - a scalar, or a class that is not generic.</summary>
    private static IReadOnlyList<TypeInfo>? RuntimeTypeArgumentsOf(object receiver)
    {
        try
        {
            return receiver switch
            {
                IScriptDictionary dict => [dict.KeyType, dict.ValueType],
                IScriptList list => [list.ElementType],
                IScriptHashSet set => [set.ElementType],
                ClassInstance { Class.TypeArguments.Count: > 0 } generic => [.. generic.Class.TypeArguments],
                Array arr when arr.GetType().GetElementType() is { } element => [MapTypeInfo(element)],
                _ => null,
            };
        }
        catch (ScriptException) { return null; }   // an exotic element type: nothing to bind from
    }

    /// <summary>Binds the block's type parameters from ARGUMENTS written for the extended type - a call site's
    /// type arguments, or a receiver's own. Matched POSITIONALLY: `extension<T>(List<T>)` takes T from
    /// argument 0, and a target like `Dictionary<string, T>` takes it from that type's second. Returns them in
    /// the BLOCK's parameter order, which is what generic instantiation expects, or null when one cannot bind.</summary>
    private IReadOnlyList<TypeInfo>? BindTypeArguments(ExtensionMemberSet set, IReadOnlyList<TypeInfo>? writtenArgs)
    {
        var targetArgs = set.Target.GenericArgs;
        if (targetArgs == null || writtenArgs == null || writtenArgs.Count != targetArgs.Count) return null;
        var bindings = new TypeInfo?[set.TypeParameters.Count];
        for (var i = 0; i < targetArgs.Count; i++)
        {
            var targetArg = targetArgs[i];
            if (!targetArg.IsGenericParameter || targetArg.TypeName == null) continue;
            for (var p = 0; p < set.TypeParameters.Count; p++)
                if (string.Equals(set.TypeParameters[p], targetArg.TypeName, Comparison))
                    bindings[p] = writtenArgs[i];
        }
        return bindings.Any(b => b == null) ? null : [.. bindings.Select(b => b!)];
    }

    /// <summary>The call-site form: a written type reference that cannot bind the block's parameters is a
    /// caller error, so this overload reports it.</summary>
    private IReadOnlyList<TypeInfo>? BindTypeArguments(ExtensionMemberSet set, TypeInfo typeRef,
        string memberName, int line, int column)
    {
        var bound = BindTypeArguments(set, typeRef.GenericArgs);
        if (bound == null)
            RuntimeError($"'{typeRef.TypeName}.{memberName}(...)' does not bind {set.TargetDisplay}'s type parameters. "
                         + "Pass the extended type's arguments at the call site.", line, column);
        return bound;
    }

    /// <summary>The static extension member `memberName` declared for a name/arity, or null.</summary>
    private FuncDecl? FindStaticExtension(string targetName, int arity, string memberName)
    {
        foreach (var (set, member) in MembersNamed(targetName, memberName))
        {
            if (set.TargetArity != arity || !member.IsStatic) continue;
            return member;
        }
        return null;
    }

    /// <summary>Invokes a static extension member reached through a type reference (`List&lt;int&gt;.Create()`).
    /// Returns false when no block declares it, so the caller reports the error in its own terms.</summary>
    private bool TryInvokeStaticExtensionOnTypeRef(TypeInfo typeRef, string memberName, object?[] args,
        int line, int column, out object? result)
    {
        result = null;
        var written = typeRef.GenericArgs;
        if (written is not { Count: > 0 }) return false;
        if (typeRef.TypeName is not { Length: > 0 } targetName) return false;
        foreach (var (set, member) in MembersNamed(targetName, memberName))
        {
            if (set.TargetArity != written.Count || !member.IsStatic) continue;
            if (member == null) continue;
            // A member of a generic block is instantiated with the call site's type arguments, so its
            // declared parameter and return types become concrete (`List<T>` -> `List<int>`), which is also
            // what lets a returned literal be target-typed by them.
            var fn = member.TypeParameters.Count > 0
                ? InstantiateGenericFunction(member, args, line, column, BindTypeArguments(set, typeRef, memberName, line, column))
                : null;
            result = fn != null
                ? InvokeExtensionMember(member, args, line, column, fn.ReturnType, fn)
                : InvokeExtensionMember(member, args, line, column);
            return true;
        }
        return false;
    }

    /// <summary>Invokes a static extension member reached through a non-generic target written as the type
    /// itself (`string.Awake()`), where the receiver is that type. Returns false when nothing declares it.</summary>
    private bool TryInvokeStaticExtensionOnType(Type receiverType, string memberName, object?[] args,
        int line, int column, out object? result)
    {
        result = null;
        if (_extensionSets.Count == 0) return false;
        // The type's own static member wins, the same rule the instance half applies to a value.
        if (OwnsMember(receiverType, memberName, args.Length, statics: true, Comparison)) return false;
        var member = FindStaticExtension(GetFriendlyTypeName(receiverType), 0, memberName);
        if (member == null) return false;
        result = InvokeExtensionMember(member, args, line, column);
        return true;
    }

    /// <summary>True when the value or type already OWNS a member of this name and arity, in which case an
    /// extension must not answer for it (C# spec 7.6.5.2): the value's own member always wins.
    ///
    /// Two traps this avoids, both of which let an extension shadow String.Trim: `GetMethod(name, flags)`
    /// THROWS AmbiguousMatchException the moment the type has two overloads of that name, and a swallowed
    /// exception then reads as "owns nothing"; and a member can own its name as a property or field rather
    /// than a method. Matching is by name and arity, the model the language's own member lookup uses, and the
    /// name comparison follows the caller's comparer, the same one the lookup it vetoes uses, so the case
    /// flag cannot be bypassed by extension dispatch. (A CLR member taking `params` or optional
    /// arguments is matched by its DECLARED arity, so a wider call is not blocked.)</summary>
    private static bool OwnsMember(Type type, string memberName, int argCount, bool statics,
        StringComparison cmp)
    {
        var flags = BindingFlags.Public | (statics ? BindingFlags.Static | BindingFlags.FlattenHierarchy
                                                   : BindingFlags.Instance);
        try
        {
            foreach (var m in type.GetMethods(flags))
                if (m.Name.Equals(memberName, cmp)
                    && m.GetParameters().Length == argCount) return true;
            if (statics) return false;
            foreach (var p in type.GetProperties(flags))
                if (p.Name.Equals(memberName, cmp)) return true;
            foreach (var f in type.GetFields(flags))
                if (f.Name.Equals(memberName, cmp)) return true;
        }
        catch { /* no metadata for this type: treat it as owning no such member */ }
        return false;
    }

    /// <summary>The instance extension member `memberName` for a receiver value, or null. The receiver's
    /// script-facing type name is matched with its type arguments ignored, so a `List<int>` value reaches
    /// `extension<T>(List<T>)`, and each of its BASE classes is tried after it, nearest first, so a `Dog` value
    /// reaches `extension(Animal)` when no block declares the member for `Dog` itself.
    /// The order IS the resolution rule: the most derived block wins, and a class has one parent, so two blocks
    /// can never be equally close to a type.</summary>
    private (ExtensionMemberSet Set, FuncDecl Member)? FindInstanceExtension(object receiver, string memberName)
    {
        foreach (var candidate in TargetCandidates(receiver))
        {
            foreach (var entry in MembersNamed(candidate, memberName))
            {
                if (!entry.Member.IsStatic) return entry;
            }
        }
        return null;
    }

    /// <summary>Memoises the INSTANCE extension lookup by (receiver CLR type, member name AS WRITTEN, arity), which
    /// is what one call site asks over and over. Measured, the lookup itself was the whole remaining cost of an
    /// extension-member call once the nested execution was removed: `TargetCandidates` names the receiver's type
    /// and `MembersNamed` materialises a list, both PER CALL, and a miss pays them too.
    ///
    /// The key is the receiver's REPORTED name AND its CLR type, and it needs both, each measured: the name
    /// separates two script classes (every `ClassInstance` shares one CLR type, so a plain base value took the
    /// DERIVED value's cached miss), while the CLR type separates two shapes that share a name (a boxed and an
    /// unboxed list both report `List<int>`, and `OwnsMember` answers differently for each). The name travels with
    /// the default (ORDINAL) comparer, so a strict lookup can never be answered by a lax spelling, and it is
    /// engine-canonical rather than script-written. The three
    /// verdicts are stored together because they are one pure function of the key plus the registry: the member (or
    /// null), the declaring block's NAMED receiver, and whether the receiver's own type already OWNS the name.
    ///
    /// Every registry CHANGE drops it, because that is the only thing that can make an entry wrong: a registration,
    /// and a clear. Both are rare, so the drop costs nothing.</summary>
    private readonly Dictionary<(string, Type, string, int), (FuncDecl? Member, string? ReceiverName, bool Owned)>
        _extensionLookup = [];

    /// <summary>Drops every memoised extension lookup (a registry change): see <see cref="_extensionLookup"/>.</summary>
    internal void ClearExtensionLookupCache() => _extensionLookup.Clear();

    /// <summary>The extended-type names a receiver can reach, NEAREST first: its own reported name with the type
    /// arguments ignored, then that same name fully written (a `List<int>` value reaching `extension<T>(List<T>)`),
    /// then each base class up the chain. A block on a base type answers only when no closer block declares the
    /// name, which is the order C# uses and the reason the walk is ordered rather than a set.</summary>
    /// <summary>Every name a block was written over, so the target walk can ask whether any of them is an INTERFACE
    /// and whether the receiver matches it. Forward references work because the question is asked at LOOKUP time,
    /// against the registry as it stands then.</summary>
    private readonly List<string> _extensionTargetNames = [];

    private List<string> TargetCandidates(object receiver)
    {
        var names = new List<string>();
        var own = GetFriendlyTypeNameOfValue(receiver);
        var bare = own.Contains('<') ? own[..own.IndexOf('<')] : own;
        AddCandidate(names, bare);
        AddCandidate(names, own);

        if (receiver is ClassInstance instance)
        {
            for (var parent = instance.Class.Parent; parent != null; parent = parent.Parent)
                AddCandidate(names, parent.Name);
        }
        else if (receiver is not StructInstance)   // a struct has no base class of its own
        {
            var type = receiver.GetType();
            if (BaseTypeNames(type) is { } chain)
                foreach (var baseName in chain) AddCandidate(names, baseName);
        }

        // A block over an INTERFACE speaks for any value that MATCHES it, nominal or structural, which is what
        // design C made meaningful. Only targets somebody wrote a block over are asked, so a script with no block
        // pays nothing; they answer after the receiver's own type and its base chain, and before `object`, which is
        // the more general spelling. In registration order, so the first block written wins when two match.
        if (_extensionTargetNames.Count > 0 && receiver is ClassInstance or StructInstance)
        {
            foreach (var targetName in _extensionTargetNames)
                if (MatchesDeclaredInterface(receiver, targetName)) AddCandidate(names, targetName);
        }

        // Every value is an object, so a block on `object` is the one spelling that can speak for any of them, and
        // it answers LAST: a nearer block always wins.
        AddCandidate(names, "object");
        return names;
    }

    /// <summary>A CLR type's base-type names, nearest first and without the type itself, cached per type: the walk
    /// runs on every miss, and the answer for a Type never changes.</summary>
    private static string[]? BaseTypeNames(Type type)
    {
        if (BaseTypeNameCache.TryGetValue(type, out var cached)) return cached;
        var names = new List<string>();
        for (var baseType = type.BaseType; baseType != null && baseType != typeof(object); baseType = baseType.BaseType)
            names.Add(GetFriendlyTypeName(baseType));
        var result = names.ToArray();
        if (BaseTypeNameCache.Count < 4096) BaseTypeNameCache[type] = result;
        return result;
    }

    private static readonly Dictionary<Type, string[]> BaseTypeNameCache = [];

    /// <summary>Adds a name once: the walk may reach the same spelling twice (a value's own name and, for a nested
    /// script class, its tail), and both spellings must be tried exactly once, in the order they were found.</summary>
    private void AddCandidate(List<string> names, string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        foreach (var existing in names)
            if (existing.Equals(name, Comparison)) return;
        names.Add(name);
        // A nested class is stored fully qualified (`Outer.Inner`); a block targets the tail.
        var dot = name.LastIndexOf('.');
        if (dot > 0)
        {
            var tail = name[(dot + 1)..];
            foreach (var existing in names)
                if (existing.Equals(tail, Comparison)) return;
            names.Add(tail);
        }
    }

    /// <summary>Invokes an instance extension member reached through a value. Returns false when nothing
    /// declares it, or when the value already owns a member of that name.</summary>
    private bool TryInvokeInstanceExtension(object? receiver, string memberName, object?[] args,
        int line, int column, out object? result)
    {
        result = null;
        if (receiver == null || _extensionSets.Count == 0) return false;
        // The set comes back WITH the member, so this does not search for its declaring block a second time.
        if (FindInstanceExtension(receiver, memberName) is not var (set, member)) return false;

        // A script-defined value matches by name and arity through its own dispatch, so this check is
        // only needed for a CLR value, which may own the name already, see OwnsMember.
        if (receiver is not (ClassInstance or StructInstance)
            && OwnsMember(receiver.GetType(), memberName, args.Length, statics: false, Comparison)) return false;

        // A generic block's T is bound from the RECEIVER, by the positional rule the static half applies to
        // the call site: a List<int> value binds T = int, so the signature and body become concrete and the
        // copy can be compiled. Only a single-parameter target can be bound this way - a two-parameter one (a
        // dictionary) has no single element type to give and is left as it was.
        // T is bound from the RECEIVER the same way the static half binds it from the call site, so a list,
        // set, array, dictionary or generic class value all give their own arguments, and one that reports
        // none simply stays as it was, with T unresolved.
        ScriptFunction? instantiated = null;
        if (!member.IsCoroutine
            && RuntimeTypeArgumentsOf(receiver) is { } runtimeArgs
            && BindTypeArguments(set, runtimeArgs) is { } instanceBindings)
            instantiated = InstantiateGenericFunction(member, args, line, column, instanceBindings, asMethod: true,
                receiverAlias: set.ReceiverName, skipVmBody: DeclaresRefOrOut(member));

        var previousThis = _currentThis;
        var previousClass = _currentClass;
        var previousReceiverName = _currentReceiverName;
        _currentThis = receiver;   // the body's `this` is the receiver
        _currentClass = null;      // an extension member belongs to no class, so bare names stay global
        // A block that NAMED its receiver (`extension(string s)`) gives its members a second spelling of the
        // same value, so the name resolves to the receiver while the member runs, the tree half of the alias
        // the compiler maps onto `this`'s slot. Null for an unnamed block, which is the common case.
        _currentReceiverName = set.ReceiverName;
        try { result = InvokeExtensionMember(member, args, line, column, instantiated?.ReturnType, instantiated, receiver); }
        finally
        {
            _currentThis = previousThis;
            _currentClass = previousClass;
            _currentReceiverName = previousReceiverName;
        }
        return true;
    }

    // ── Extension PROPERTIES ────────────────────────────────────────────────────────────────────────
    // A property is a member one level up, and it reuses the member path end to end: the accessors are ordinary
    // FuncDecls, so InvokeExtensionMember runs them (flat on the VM when it can, interpreted when it must) and the
    // only new machinery is the lookup and the two hooks the property read/write paths call when they MISS.

    /// <summary>A property read through the extension registry, the LAST resort of the property read path, after
    /// every real member has had its chance, which is the order C# uses and the order the method half uses. The
    /// receiver may be a VALUE (the instance half) or a TYPE (the static half, `List&lt;int&gt;.Count`).</summary>
    internal bool TryGetExtensionProperty(object? receiver, string propertyName, int line, int column,
        out object? value)
    {
        value = null;
        if (receiver == null || _extensionSets.Count == 0) return false;
        var property = FindExtensionProperty(receiver, propertyName, out var set);
        if (property == null) return false;
        // Declared, but nothing to read through: a set-only property. Reporting "not found" would send a reader
        // looking for a typo in a name that is spelled right.
        if (property.Getter == null)
            RuntimeError($"Property '{propertyName}' has no getter.", line, column, ErrorCode.AccessViolation);
        value = InvokeExtensionAccessor(receiver, set!, property, property.Getter, [], line, column, propertyName);
        return true;
    }

    /// <summary>A property write through the extension registry, the mirror of the read hook. Returns false when
    /// no extension declares the property, the caller reports the miss in its own terms, and raises when the
    /// property is declared READ-ONLY, because "not found" would be the wrong story there.</summary>
    internal bool TrySetExtensionProperty(object? receiver, string propertyName, object? assigned, int line, int column,
        out object? result)
    {
        result = null;
        if (receiver == null || _extensionSets.Count == 0) return false;
        var property = FindExtensionProperty(receiver, propertyName, out var set);
        if (property == null) return false;
        if (property.Setter == null)
            RuntimeError($"Property '{propertyName}' has no setter.", line, column, ErrorCode.AccessViolation);
        result = InvokeExtensionAccessor(receiver, set!, property, property.Setter, [assigned], line, column, propertyName);
        return true;
    }

    /// <summary>The property a receiver's name resolves to, with the set that declares it: the instance half for a
    /// value, the static half for a type. Null when no block declares it for this receiver. The caller picks the
    /// accessor it needs, so a getter-only and a set-only property are each refused in the terms of the ask.</summary>
    private ExtensionPropertyDecl? FindExtensionProperty(object receiver, string propertyName,
        out ExtensionMemberSet? set)
    {
        set = null;
        if (IsTypeHalfReceiver(receiver))
        {
            foreach (var candidate in _extensionSets)
            {
                if (!StaticTargetMatches(candidate, receiver)) continue;
                foreach (var declared in candidate.Properties)
                {
                    if (!declared.IsStatic || !declared.Name.Equals(propertyName, Comparison)) continue;
                    set = candidate;
                    return declared;
                }
            }
            return null;
        }

        // A VALUE walks its own name and then its base classes, nearest first: the same ordered rule the member
        // half uses, so a property declared for a base class answers only where the value's own name does not.
        foreach (var name in TargetCandidates(receiver))
        {
            foreach (var candidate in _extensionSets)
            {
                if (!candidate.TargetName.Equals(name, Comparison)) continue;
                foreach (var declared in candidate.Properties)
                {
                    if (declared.IsStatic || !declared.Name.Equals(propertyName, Comparison)) continue;
                    set = candidate;
                    return declared;
                }
            }
        }
        return null;
    }

    /// <summary>True when a parameter's declared type is the block's target: the same name and arity rule the
    /// static half applies to a written type, which is what makes an operator findable by its operands.</summary>
    private bool ParameterIsTarget(TypeInfo parameterType, string targetName, int targetArity)
    {
        var written = parameterType.TypeName ?? TypeInfo.SimpleTypeName(parameterType.BaseType);
        if (!written.Equals(targetName, Comparison)) return false;
        return (parameterType.GenericArgs?.Count ?? 0) == targetArity;
    }
    /// <summary>True when the receiver stands for a TYPE rather than a value: a `Type`, a `TypeInfo` written as a
    /// type reference (`List&lt;int&gt;`), or a script `ClassInfo` reached through its class name. Those reach the
    /// STATIC half of a block, exactly as they do on the member path.</summary>
    private static bool IsTypeHalfReceiver(object receiver) => receiver is TypeInfo or Type or ClassInfo;

    /// <summary>Applies an operator an extension block declares. It is reached the way C# reaches a user-defined
    /// operator: by the OPERAND types, after the operands'' own types have had their chance, and the walk of each
    /// operand is the same nearest-first one the member half uses, so a block on a base type is reachable too.
    /// Returns false when no block declares one for these operands, so the caller keeps its built-in meaning.</summary>
    internal bool TryInvokeExtensionOperator(TokenType op, object? left, object? right, int line, int column,
        out object? result)
    {
        result = null;
        if (_extensionSets.Count == 0) return false;
        foreach (var operand in new[] { left, right })
        {
            if (operand == null || IsTypeHalfReceiver(operand)) continue;
            foreach (var candidate in TargetCandidates(operand))
            {
                foreach (var set in _extensionSets)
                {
                    if (!set.TargetName.Equals(candidate, Comparison)) continue;
                    if (OperatorFor(set, op) is not { } declared) continue;
                    result = InvokeExtensionOperatorMember(set, declared, [left, right], line, column);
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>The operator a block declares for a token, or null. A block declares one per token, which the
    /// registration enforces, so no two candidates can be equally close for one token.</summary>
    private static FuncDecl? OperatorFor(ExtensionMemberSet set, TokenType op)
    {
        foreach (var declared in set.Operators)
            if (declared.OperatorToken == op) return declared;
        return null;
    }

    /// <summary>Runs an operator''s body with both operands. A generic block binds its own type parameters from the
    /// first operand that reports them, the same way a property binds from its receiver.</summary>
    private object? InvokeExtensionOperatorMember(ExtensionMemberSet set, FuncDecl declared, object?[] args,
        int line, int column)
    {
        ScriptFunction? instantiated = null;
        if (declared.TypeParameters.Count > 0)
        {
            foreach (var operand in args)
            {
                if (operand == null || RuntimeTypeArgumentsOf(operand) is not { } runtimeArgs) continue;
                if (BindTypeArguments(set, runtimeArgs) is not { } bindings) continue;
                instantiated = InstantiateGenericFunction(declared, args, line, column, bindings);
                break;
            }
        }
        return InvokeExtensionMember(declared, args, line, column, instantiated?.ReturnType, instantiated);
    }
    /// <summary>True when a block's target is the TYPE a receiver stands for: the written name and its type
    /// arguments, which is what a static half matches. A VALUE is matched by name only, through the ordered walk
    /// (its own name, then its base classes), so the two halves ask different questions and say so.</summary>
    private bool StaticTargetMatches(ExtensionMemberSet set, object receiver)
    {
        if (receiver is ClassInfo staticClass)
        {
            // A script class name is not a CLR type, and the value naming helper would call it "ClassInfo", so
            // the declaration's own name is what a block targets, tail-qualified for a nested one.
            if (set.TargetArity != 0) return false;
            return set.TargetName.Equals(staticClass.Name, Comparison)
                   || staticClass.Name.EndsWith("." + set.TargetName, Comparison);
        }
        var writtenCount = receiver is TypeInfo { GenericArgs: { } args } ? args.Count : 0;
        if (set.TargetArity != writtenCount) return false;
        var typeName = receiver is TypeInfo typeInfo
            ? typeInfo.TypeName ?? TypeInfo.SimpleTypeName(typeInfo.BaseType)
            : GetFriendlyTypeName((Type)receiver);
        return set.TargetName.Equals(typeName, Comparison);
    }

    /// <summary>Runs one accessor of an extension property, with the receiver when it has one and the set's type
    /// arguments bound when the block is generic. `args` is empty for a getter and holds the assigned value for a
    /// setter.</summary>
    private object? InvokeExtensionAccessor(object receiver, ExtensionMemberSet set, ExtensionPropertyDecl property,
        FuncDecl accessor, object?[] args, int line, int column, string propertyName)
    {
        ScriptFunction? instantiated = null;
        if (!accessor.IsCoroutine && accessor.TypeParameters.Count > 0)
        {
            // A member of a generic block is instantiated with the receiver's own type arguments (or the
            // call site's, for the static half), the same binding the method half asks for.
            var bindings = receiver is TypeInfo typeRef
                ? BindTypeArguments(set, typeRef, propertyName, line, column)
                : RuntimeTypeArgumentsOf(receiver) is { } runtimeArgs ? BindTypeArguments(set, runtimeArgs) : null;
            if (bindings != null)
                instantiated = InstantiateGenericFunction(accessor, args, line, column, bindings,
                    asMethod: !accessor.IsStatic, receiverAlias: set.ReceiverName);
        }

        var previousThis = _currentThis;
        var previousClass = _currentClass;
        var previousReceiverName = _currentReceiverName;
        if (!accessor.IsStatic)
        {
            _currentThis = receiver;   // the accessor's `this` is the receiver, like a member's
            _currentClass = null;
            _currentReceiverName = set.ReceiverName;
        }
        try
        {
            return InvokeExtensionMember(accessor, args, line, column, instantiated?.ReturnType, instantiated,
                accessor.IsStatic ? null : receiver);
        }
        finally
        {
            _currentThis = previousThis;
            _currentClass = previousClass;
            _currentReceiverName = previousReceiverName;
        }
    }

    /// <summary>The one way an extension member body is invoked. It is a script function like any method, so
    /// it closes over its declaration site exactly as a static method of a class does.</summary>
    private object? InvokeExtensionMember(FuncDecl member, object?[] args, int line, int column,
        TypeInfo? declaredReturnType = null, ScriptFunction? instantiated = null, object? receiver = null)
    {
        var declared = declaredReturnType ?? member.ReturnType;

        // An ASYNC member is wrapped the same way: the receiver becomes ThisInstance and the body's `this` local,
        // and the caller receives the Task rather than a Generator. Its body is never compiled (it is IsCoroutine,
        // and the compiler declines an async body), so this path is the tree.
        if (member.IsAsync)
            return BeginAsyncMethodCall(member, args, receiver!, null);

        // A coroutine member is wrapped the way a coroutine METHOD is: the receiver becomes ThisInstance and the
        // body's `this` local, and the interpreter walks it (a yielding body is never run flat).
        if (member.IsCoroutine)
            return WrapCoroutineMethod(member, args, receiver!, null, line, column);

        // A member with bytecode runs flat, mirroring the method path: [params..., this] plus Vm.Execute,
        // with the tree invocation as the fallback when the VM hands the call back.
        //
        // The receiver reaches a COMPILED body as the TRAILING local, which is why this path builds its arguments
        // that way, and why every shape that skipped it used to lose `this` silently: a `params` member and a
        // defaulted parameter both make args.Length != Params.Count, so the flat path was declined and the fallback
        // ran the same compiled body with no receiver. Measured before the fix: `Params2(params int[] xs) => this +
        // ":" + xs.Length` rendered ":2" on a string receiver, the concatenation of a NULL `this`, while the same
        // member called one-to-one kept its receiver. So the arguments are NORMALISED first (params packed,
        // defaults filled), which makes this path applicable to those shapes as well.
        var vmBody = instantiated?.Declaration?.Compiled ?? member.Compiled;
        var noRef = (member.IsRefParams == null || !member.IsRefParams.Any(b => b))
                    && (member.IsOutParams == null || !member.IsOutParams.Any(b => b));
        var flatArgs = args;
        if (receiver != null && vmBody != null && !member.IsCoroutine && noRef)
            flatArgs = NormalizeArgsForVm(member, args, line, column);
        if (receiver != null && vmBody != null && !member.IsCoroutine
            && flatArgs.Length == member.Params.Count && noRef)
        {
            var vmArgs = BuildCtorVmArgs(flatArgs, receiver);
            // Same rule as TryExecuteVmBody: the tree fallback below runs the member's AST from its FIRST statement, so
            // a VM that wrote a global or a property before stopping would have that work repeated. Refuse instead.
            var extCommitBaseline = VmBodyCommitted;
            VmBodyCommitted = false;
            object? vmResult;
            var extCommittedHere = false;
            try
            {
                vmResult = Vm.Execute(vmBody, vmArgs, this, "Extension." + member.Name);
            }
            finally
            {
                extCommittedHere = VmBodyCommitted;
                VmBodyCommitted = extCommitBaseline || extCommittedHere;
            }
            if (vmResult is VmFallback && extCommittedHere)
                RuntimeError($"Extension member '{member.Name}' stopped part-way through its compiled body, at an instruction the VM cannot run here. Running it on the tree from the start would repeat everything it had already done, so it is refused: this is an engine gap rather than a mistake in the script.", line, column);
            if (vmResult is not VmFallback)
            {
                VmInvocations++;
                return IsDirectLiteralReturn(member)
                    ? TargetTypedLiteralResult(declared, vmResult, line, column)
                    // Every OTHER shape gets the same coercion a function's return gets: this entry had none, so
                    // an extension member declared `int` answered 1.5 where the tree answers 2, and a wrong type
                    // travelled back untouched (measured: `extension(string) { int ExtInt() { return 1.5; } }`).
                    : CoerceFlatCallResult(member, vmResult, line, column);
            }
        }

        // `?? []`: a declaration that recorded no ref-parameters recorded none. That is the same null-or-empty
        // the compiler path already treats as "no ref parameters" (Compiler.cs reads `IsRefParams is { Count:
        // > 0 }`), and the property is declared `= null!`, so the compiler cannot see that empty is the real
        // case for a member declared without any.
        // The closure snapshot and the parameter types belong HERE, to the tree fallback, and not to the top of the
        // method: the flat path returns before this, and this is a member-MISS path, so building them for a COMPILED
        // body was a fixed allocation per call (measured: tens of kilobytes, and it grew with the global scope).
        // THE CAPTURE-FREE SNAPSHOT, exactly as a STATIC METHOD is called: an extension member's body closes over its
        // DECLARATION site, never over the caller, so it has nothing of the caller's to capture. That is not a
        // preference: `CaptureClosureSnapshotWithGlobal` leaves the snapshot's GlobalScope EMPTY by contract and
        // InvokeScriptFunction refreshes the readable globals ONLY for a snapshot with NO local scopes, so capturing
        // the caller's scopes here produced a body that could see NEITHER the caller's locals (it never should) NOR any
        // global (measured: `extension(string s3) { set { seen13 = value; } }` answered "Undefined variable 'seen13'"
        // with `rootHas=True, snapLocals=1, snapGlobalsHas=False`, while the same code with VmSetEnabled(false)
        // answered 77). With no local scopes the refresh runs and the body reads the live globals, which is what the
        // static-method arm does for the same reason.
        var snapshot = SEmptySnapshot;
        var paramTypes = GetParameterTypes(member.Params);
        var fn = instantiated ?? new ScriptFunction(member, snapshot, member.ReturnType, paramTypes,
            member.IsRefParams ?? [], IsOutParams: member.IsOutParams);
        var result = InvokeScriptFunction(fn, args, line, column);
        // C# 14's collection expressions, in the one shape the declared type governs: a body that is nothing
        // but a returned literal takes its container from the (already substituted) return type.
        return IsDirectLiteralReturn(member) ? TargetTypedLiteralResult(declared, result, line, column) : result;
    }

    /// <summary>Reports a member miss, unless a script extension member declares that member for the
    /// receiver. The dispatch arms that report a miss all funnel through here, so the extension half is
    /// checked in ONE place even though the arms that reach it are many.</summary>
    private object? MissingMemberOrExtension(object? receiver, string memberName, object?[] args,
        int line, int column, string missMessage)
    {
        // An interface DEFAULT is nearer than a block's member: it is part of the receiver's own contract, so it
        // is asked for first, and only where every real member has already declined.
        if (TryInvokeInterfaceDefault(receiver, memberName, args, line, column, out var defaultResult))
            return defaultResult;
        if (TryInvokeInstanceExtension(receiver, memberName, args, line, column, out var extensionResult))
            return extensionResult;
        return RuntimeError<object?>(missMessage, line, column);
    }

    /// <summary>The ONE wording for "this value has no such member", with the type named as a SCRIPT writes it,
    /// through `ReportedTypeName` (`int[]`, `List<int>`, `Dictionary<string, int>`, `string`). Both
    /// halves were wrong per value kind before this: measured, `.Nope()` on an array answered "Method 'Nope' not
    /// found on type Int32[] with 0 argument(s)", on a List "List does not contain method 'Nope'", and on a
    /// dictionary "Dictionary does not contain method 'Nope'", while `typeof` reported `int[]`, `List<int>` and
    /// `Dictionary<string, int>` for the same three values. A reported name is always one a script can write.</summary>
    internal static string MissingMemberText(object? receiver, string memberName, int argCount)
        => $"Method '{memberName}' not found on type {ReportedTypeName(receiver)} with {argCount} argument(s).";

    /// <summary>The message for a static member no extension block declares, naming the declaration the
    /// script would have to add.</summary>
    private static string NoStaticExtensionMessage(string target, string memberName) =>
        $"No static extension member '{memberName}' for '{target}'. Declare one with "
        + $"extension<T>({target}) {{ public static T {memberName}(...) }}.";

    public object? VisitTypeRefExpr(TypeRefExpr node) => node.Type;

    public ExecutionResult VisitExtensionDeclStmt(ExtensionDeclStmt node)
    {
        RegisterExtensionMembers(node.TypeParameters, node.Target, node.Members, node.ContainerName, node.ReceiverName,
            node.Properties, node.Operators);
        return ExecutionResult.Continue();
    }
}
