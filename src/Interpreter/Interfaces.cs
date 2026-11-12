namespace ScriptLangCore;

// ============================================================================
// `interface`: a NAMED contract of method signatures (design B of P7)
//
//   interface IShape {
//       int area();                                   // abstract: the class must implement it
//       string describe() { return "a shape"; }        // default: inherited when nothing answers
//   }
//   interface IScalable extends IShape { void scale(int k); }
//   class Circle implements IShape, IScalable { ... }
//
// It is NOMINAL: a class declares what it implements, the check runs when the class declaration executes
// (before any instance exists), and `is`/`as`/a cast is a membership lookup rather than a shape test. Dispatch
// stays the engine's normal name lookup, so there is no slot table and no new opcode, and a member with a
// default body is served by the same miss path that already serves an extension member.
// ============================================================================

/// <summary>One declared interface: its name, the interfaces it extends (as written), that set FLATTENED with
/// itself first, the members it pins as (name, arity), and the members that carry a default body keyed by
/// `name/arity`.</summary>
internal sealed record InterfaceInfo(
    string Name,
    IReadOnlyList<string> Parents,
    IReadOnlyList<string> Flattened,
    IReadOnlyList<(string Name, int Arity)> Members,
    IReadOnlyDictionary<string, FuncDecl> Defaults,
    int Line,
    int Column)
{
    /// <summary>The members a MATCHING value has to PROVIDE: the flattened set, minus the ones that carry a
    /// default body, because the interface itself supplies those (the miss path serves them). This is what the
    /// structural reading asks a value for, and it is computed once, at registration.</summary>
    public IReadOnlyList<(string Interface, string Name, int Arity)> Required { get; init; } = [];
    /// <summary>The properties it pins, with any accessor bodies as defaults.</summary>
    public IReadOnlyList<InterfacePropertySignature> Properties { get; init; } = [];
    /// <summary>The PROPERTY demands a matching value must satisfy: the flattened set minus the ones an
    /// interface gives a body for. NeedsGetter/NeedsSetter are the SHAPE asked of the member a value provides,
    /// because a property is reached here through its NAME on the value, as a field or as a script property.</summary>
    public IReadOnlyList<(string Interface, string Name, bool NeedsGetter, bool NeedsSetter)> RequiredProperties { get; init; } = [];
    /// <summary>The type parameters this contract was declared WITH, empty for a non-generic one.</summary>
    public IReadOnlyList<string> TypeParameters { get; init; } = [];
    /// <summary>The arguments a class gave it, for an instantiation such as <code>I&lt;int&gt;</code>.</summary>
    public IReadOnlyList<string> TypeArguments { get; init; } = [];
    /// <summary>True for a closed generic contract: matched by DECLARATION only, because a shape cannot witness
    /// which arguments were written.</summary>
    public bool IsGenericInstantiation { get; init; }
}

internal partial class Interpreter
{
    /// <summary>Every declared interface, by name, following the interpreter's case flag.</summary>
    private readonly Dictionary<string, InterfaceInfo> _interfaces = [];

    /// <summary>The declared interfaces in DECLARATION order, which is the order a structural default is looked
    /// for in, so the choice is deterministic when two of them provide the same member.</summary>
    private readonly List<string> _interfaceOrder = [];
    /// <summary>A contract declared with type parameters is a TEMPLATE, not something a value can match: a
    /// class instantiates it in `implements` (`I<int>`), and the closed form lands in <see cref="_interfaces"/>.</summary>
    private readonly Dictionary<string, InterfaceInfo> _genericInterfaceTemplates = [];

    /// <summary>The contract whose body is running, if any. A DEFAULT body composes through its OWN contract:
    /// `this.other()` inside `IShape.describe` means the IShape meaning of `other`, which is how one interface
    /// method reaches another the class implemented EXPLICITLY.</summary>
    private string? _currentContract;

    /// <summary>Forgets every declared interface. A re-run of a FILE is a fresh declaration unit, exactly like
    /// the extension sets: `.looprun` re-reads the source, so the same interface arrives again by design.</summary>
    internal void ClearInterfaces()
    {
        _interfaces.Clear();
        _interfaceOrder.Clear();
        _genericInterfaceTemplates.Clear();
    }

    public ExecutionResult VisitInterfaceDeclStmt(InterfaceDeclStmt node)
    {
        RegisterInterface(node);
        return ExecutionResult.Continue();
    }

    private void RegisterInterface(InterfaceDeclStmt node)
    {
        if (ContractTargetNames.Any(c => c.Equals(node.Name, Comparison)))
            throw new ScriptException(
                $"'{node.Name}' is a built-in CONTRACT, a guard for `is` and `as`, so it cannot be declared as an "
                + "interface. Choose another name.");

        var flattened = new List<string> { node.Name };
        foreach (var parent in node.Parents)
        {
            if (!TryResolveInterface(parent, out var parentInfo) || parentInfo == null)
                throw new ScriptException(
                    $"Interface '{node.Name}' extends '{parent}', which is not declared. An interface declaration "
                    + "is a statement, so declare it before the interface that extends it.");
            if (parentInfo.Name.Equals(node.Name, Comparison) || parentInfo.Flattened.Contains(node.Name, _comparer))
                throw new ScriptException(
                    $"Interface '{node.Name}' extends '{parent}', which already includes '{node.Name}': an "
                    + "interface cycle is refused.");
            foreach (var name in parentInfo.Flattened)
                if (!flattened.Contains(name, _comparer)) flattened.Add(name);
        }

        // What a MATCHING value must provide: every demanded member that no interface in the chain gives a
        // default body for. The chain is walked, because a default in an ancestor satisfies a demand too.
        var required = new List<(string Interface, string Name, int Arity)>();
        foreach (var name in flattened)
        {
            var contract = name.Equals(node.Name, Comparison) ? null : _interfaces[name];
            var members = contract?.Members ?? node.Members;
            var defaults = contract?.Defaults ?? node.Defaults;
            foreach (var (memberName, arity) in members)
            {
                if (defaults.ContainsKey(memberName + "/" + arity)) continue;
                if (!required.Any(r => r.Name.Equals(memberName, Comparison) && r.Arity == arity))
                    required.Add((name, memberName, arity));
            }
        }

        // A QUALIFIED member in an interface body overrides an ancestor's default, so the contract it names must be
        // one this interface extends, and one that declares that member. Where it does, the override is what a class
        // implementing this interface gets, because the flattened chain is walked nearest first.
        foreach (var (key, member) in node.Defaults)
        {
            if (member.Qualifier == null) continue;
            if (member.Qualifier.Equals(node.Name, Comparison))
                throw new ScriptException(
                    $"Interface '{node.Name}' cannot override its own '{key}': write that member unqualified.");
            if (!flattened.Contains(member.Qualifier, _comparer) || !_interfaces.ContainsKey(member.Qualifier))
                throw new ScriptException(
                    $"Interface '{node.Name}' overrides '{key}' for '{member.Qualifier}', which it does not extend. "
                    + $"Extend it (`interface {node.Name} extends {member.Qualifier}`) or drop the qualifier.");
            var owner = _interfaces[member.Qualifier];
            if (!owner.Members.Any(m => m.Name.Equals(member.Name, Comparison) && m.Arity == member.Params.Count))
                throw new ScriptException(
                    $"Interface '{node.Name}' overrides '{member.Qualifier}.{member.Name}/{member.Params.Count}', "
                    + $"which '{member.Qualifier}' does not declare. Name a member it declares, or write this one "
                    + "without a qualifier to add a NEW member.");
        }
        // The PROPERTY demands: a body on an accessor satisfies that side, an abstract one is what a value must
        // provide, and the walk is nearest-first for the same reason the members are.
        var requiredProperties = new List<(string Interface, string Name, bool NeedsGetter, bool NeedsSetter)>();
        foreach (var name in flattened)
        {
            var contractProperties = name.Equals(node.Name, Comparison) ? node.Properties : _interfaces[name].Properties;
            foreach (var property in contractProperties)
            {
                if (property.HasGetter && property.Getter == null
                    && !requiredProperties.Any(r => r.Name.Equals(property.Name, Comparison) && r.NeedsGetter))
                    requiredProperties.Add((name, property.Name, true, false));
                if (property.HasSetter && property.Setter == null
                    && !requiredProperties.Any(r => r.Name.Equals(property.Name, Comparison) && r.NeedsSetter))
                    requiredProperties.Add((name, property.Name, false, true));
            }
        }
        var info = new InterfaceInfo(node.Name, node.Parents, flattened, node.Members, node.Defaults,
            node.Line, node.Column)
        {
            Required = required,
            Properties = node.Properties,
            RequiredProperties = requiredProperties,
            TypeParameters = node.TypeParameters
        };
        if (node.TypeParameters.Count > 0)
        {
            if (!_genericInterfaceTemplates.TryAdd(node.Name, info))
                Logging.Log.Warning("Script", $"Generic interface '{node.Name}' already defined. Skipping.",
                    node.Line, node.Column);
            return;
        }        // A default's body is compiled HERE, exactly as a block's members are: a default is served on the member-MISS
        // path, and an interpreted body there costs tens of kilobytes per call against a few for a compiled one
        // (measured). A GENERIC contract is left interpreted, because instantiation is interpreter work, which is the
        // same rule the extension registration follows.
        if (node.TypeParameters.Count == 0)
        {
            foreach (var defaultBody in node.Defaults.Values)
                if (defaultBody.Compiled == null)
                    defaultBody.Compiled = CompileExtensionMemberBody(defaultBody, null);
            foreach (var property in node.Properties)
            {
                if (property.Getter is { Compiled: null } getter)
                    getter.Compiled = CompileExtensionMemberBody(getter, null);
                if (property.Setter is { Compiled: null } setter)
                    setter.Compiled = CompileExtensionMemberBody(setter, null);
            }
        }
        if (_interfaces.TryAdd(node.Name, info))
        {
            _interfaceOrder.Add(node.Name);
        }
        else
        {
            // The class precedent: a re-declaration keeps the first and says so, because a contract a class was
            // already checked against must not be swapped under it.
            Logging.Log.Warning("Script", $"Interface '{node.Name}' already defined. Skipping.", node.Line, node.Column);
        }
    }

    /// <summary>Validates a class against every interface it declares and records the FLATTENED membership set on
    /// it. This is the finalization: it runs when the class declaration executes, so a missing member is reported
    /// at the declaration, with the name and the arity, rather than at the first call.</summary>
    private ClassInfo ApplyClassInterfaces(ClassDeclStmt node, ClassInfo classInfo)
    {
        if (node.Interfaces.Count == 0) return classInfo;

        var implemented = new List<string>();
        if (classInfo.Parent?.ImplementedInterfaces is { Count: > 0 } inherited)
            foreach (var name in inherited)
                if (!implemented.Contains(name, _comparer)) implemented.Add(name);

        foreach (var declared in node.Interfaces)
        {
            var info = ResolveInterface(declared);
            if (info == null)
                throw new ScriptException(
                    $"Class '{classInfo.Name}' implements '{declared}', which is not declared. Declare the "
                    + "interface before the class that implements it.");

            foreach (var demanded in info.Flattened)
            {
                var contract = ResolveInterface(demanded)!;
                foreach (var (memberName, arity) in contract.Members)
                {
                    if (HasImplementation(classInfo, demanded, memberName, arity)) continue;
                    if (contract.Defaults.ContainsKey(memberName + "/" + arity)) continue;
                    throw new ScriptException(
                        $"Class '{classInfo.Name}' implements '{demanded}' but does not implement "
                        + $"'{memberName}/{arity}'. Add 'public <type> {memberName}({ArityRecipe(arity)}) {{ }}' to "
                        + $"'{classInfo.Name}', or give '{demanded}.{memberName}/{arity}' a default body.");
                }
            }
            foreach (var flattenedName in info.Flattened)
                if (!implemented.Contains(flattenedName, _comparer)) implemented.Add(flattenedName);
        }

        // The PROPERTY demands of every declared contract, checked the same way the members are: a value must
        // provide a member of that NAME with the shape asked for, and an accessor BODY is what satisfies a side.
        foreach (var declared in node.Interfaces)
        {
            var declaredInfo = _interfaces[declared];
            foreach (var demanded in declaredInfo.Flattened)
            {
                foreach (var property in ResolveInterface(demanded)!.Properties)
                {
                    var needsGetter = property.HasGetter && property.Getter == null;
                    var needsSetter = property.HasSetter && property.Setter == null;
                    if (!needsGetter && !needsSetter) continue;
                    if (ProvidesProperty(classInfo, property.Name, needsGetter, needsSetter)) continue;
                    throw new ScriptException(
                        $"Class '{classInfo.Name}' implements '{demanded}' but does not provide the property "
                        + $"'{property.Name}' ({DescribePropertyShape(needsGetter, needsSetter)}). Add a field or a "
                        + $"property named '{property.Name}' to '{classInfo.Name}', or give "
                        + $"'{demanded}.{property.Name}' a default body.");
                }
            }
        }
        return classInfo with { ImplementedInterfaces = implemented };
    }

    /// <summary>Validates a struct against the contracts it declares, exactly as a class is validated, and records
    /// the flattened set. A struct has no base to inherit from, so its own members are the whole surface a promise
    /// can be checked against.</summary>
    private void ApplyStructInterfaces(StructDeclStmt declaration)
    {
        if (declaration.Interfaces.Count == 0) return;
        var implemented = new List<string>();
        foreach (var declared in declaration.Interfaces)
        {
            var info = ResolveInterface(declared);
            if (info == null)
                throw new ScriptException(
                    $"Struct '{declaration.Name}' implements '{declared}', which is not declared. Declare the "
                    + "interface before the struct that implements it.");
            foreach (var demanded in info.Flattened)
            {
                var contract = ResolveInterface(demanded)!;
                foreach (var (memberName, arity) in contract.Members)
                {
                    if (StructProvides(declaration, demanded, memberName, arity)) continue;
                    if (contract.Defaults.ContainsKey(memberName + "/" + arity)) continue;
                    throw new ScriptException(
                        $"Struct '{declaration.Name}' implements '{demanded}' but does not implement "
                        + $"'{memberName}/{arity}'. Add 'public <type> {memberName}({ArityRecipe(arity)}) {{ }}' to "
                        + $"'{declaration.Name}', or give '{demanded}.{memberName}/{arity}' a default body.");
                }
            }
            foreach (var flattenedName in info.Flattened)
                if (!implemented.Contains(flattenedName, _comparer)) implemented.Add(flattenedName);
        }
        foreach (var declared in declaration.Interfaces)
        {
            var declaredInfo = _interfaces[declared];
            foreach (var demanded in declaredInfo.Flattened)
            {
                foreach (var property in ResolveInterface(demanded)!.Properties)
                {
                    var needsGetter = property.HasGetter && property.Getter == null;
                    var needsSetter = property.HasSetter && property.Setter == null;
                    if (!needsGetter && !needsSetter) continue;
                    if (ProvidesProperty(declaration.Fields, declaration.Properties, property.Name, needsGetter,
                                         needsSetter)) continue;
                    throw new ScriptException(
                        $"Struct '{declaration.Name}' implements '{demanded}' but does not provide the property "
                        + $"'{property.Name}' ({DescribePropertyShape(needsGetter, needsSetter)}). Add a field or a "
                        + $"property named '{property.Name}' to '{declaration.Name}', or give "
                        + $"'{demanded}.{property.Name}' a default body.");
                }
            }
        }
        declaration.ImplementedInterfaces = implemented;
    }
    /// <summary>What a property demand asks of the member a value provides, in the words of a message.</summary>
    private static string DescribePropertyShape(bool needsGetter, bool needsSetter)
        => (needsGetter, needsSetter) switch
        {
            (true, true) => "readable and writable",
            (true, false) => "readable",
            (false, true) => "writable",
            _ => "present"
        };
    /// <summary>A readable parameter recipe for a message: an interface pins an arity, so the names are not
    /// known (and an empty list is what a zero-arity member needs).</summary>
    private static string ArityRecipe(int arity)
        => arity == 0 ? "" : string.Join(", ", Enumerable.Range(1, arity).Select(i => "arg" + i));

    /// <summary>True when the class, or any of its base classes, implements a member of that name and arity. The
    /// walk is the point: an inherited implementation satisfies the contract, which is C#'s rule too.</summary>
    private bool HasImplementation(ClassInfo classInfo, string contractName, string memberName, int arity)
    {
        for (var current = classInfo; current != null; current = current.Parent)
        {
            foreach (var method in current.Methods)
                if (!method.IsStatic && method.Name.Equals(memberName, Comparison)
                    && method.Params.Count == arity)
                    return true;
            // An EXPLICIT implementation satisfies only the contract it is qualified with: that is the whole
            // point of one, and it is why two contracts may demand the same name with different bodies.
            foreach (var explicitMember in current.ExplicitMembers)
                if (!explicitMember.IsStatic && explicitMember.Name.Equals(memberName, Comparison)
                    && explicitMember.Params.Count == arity
                    && explicitMember.Qualifier != null
                    && explicitMember.Qualifier.Equals(contractName, Comparison))
                    return true;
        }
        return false;
    }

    /// <summary>True when a value's class DECLARED this interface, which is the promise `implements` makes.</summary>
    private bool ImplementsInterface(ClassInstance instance, string interfaceName)
        => instance.Class.ImplementedInterfaces.Contains(interfaceName, _comparer);

    /// <summary>The STRUCTURAL reading: a value matches a declared interface when it PROVIDES every member the
    /// interface demands, whether or not its class declared `implements` (design C). A member with a default body
    /// is not asked for, because the interface supplies it. Only a script class or struct can match: a library
    /// value's surface belongs to the library, so it does not answer a contract this language declares.</summary>
    private bool MatchesDeclaredInterface(object? value, string interfaceName)
    {
        if (!TryResolveInterface(interfaceName, out var info) || info == null) return false;
        // A GENERIC contract is matched by DECLARATION only: the arguments are part of what a class wrote, and a
        // shape cannot witness them, so `is I<int>` answers from what a class declared rather than from its
        // members. This is the one place the structural reading deliberately does not reach.
        if (info.IsGenericInstantiation)
            return value switch
            {
                ClassInstance declaredInstance => ImplementsInterface(declaredInstance, interfaceName),
                StructInstance declaredStruct
                    when Structs.TryGetValue(declaredStruct.StructName, out var structDeclaration)
                    => structDeclaration.ImplementedInterfaces.Contains(interfaceName, _comparer),
                _ => false
            };
        if (value is ClassInstance classInstance)
        {
            foreach (var (contract, name, arity) in info.Required)
                if (!HasImplementation(classInstance.Class, contract, name, arity)) return false;
            return true;
        }
        if (value is StructInstance structInstance)
        {
            // The registry is the authority, not the instance's own back-pointer: every construction path
            // registers the declaration, and one of them may not carry it on the value.
            if (!Structs.TryGetValue(structInstance.StructName, out var declaration)) return false;
            foreach (var (contract, name, arity) in info.Required)
                if (!StructProvides(declaration, contract, name, arity)) return false;
            return true;
        }
        return false;
    }

    /// <summary>True when the struct declares a member of that name and arity. A struct has no base to inherit
    /// from, so its own declarations are the whole surface a contract can be matched against.</summary>
    private bool StructProvides(StructDeclStmt declaration, string contractName, string memberName, int arity)
    {
        foreach (var method in declaration.Methods)
            if (!method.IsStatic && method.Name.Equals(memberName, Comparison) && method.Params.Count == arity
                && (method.Qualifier == null || method.Qualifier.Equals(contractName, Comparison)))
                return true;
        foreach (var explicitMember in declaration.ExplicitMembers)
            if (!explicitMember.IsStatic && explicitMember.Name.Equals(memberName, Comparison)
                && explicitMember.Params.Count == arity
                && explicitMember.Qualifier != null
                && explicitMember.Qualifier.Equals(contractName, Comparison))
                return true;
        return false;
    }

    /// <summary>The DEFAULT body a class inherits for a member it does not implement, or null when no interface it
    /// implements declares one. Called from the member miss path, so every real member has already had its chance:
    /// a default answers where nothing else does, exactly as C# resolves one.</summary>
    private bool TryInvokeInterfaceDefault(object? receiver, string memberName, object?[] args, int line,
        int column, out object? result)
    {
        result = null;
        var key = memberName + "/" + args.Length;

        // A DECLARED contract first, in the order it was written. If TWO of them provide a default for this member
        // the call is AMBIGUOUS: C# refuses until the class implements the member, and silently picking one of two
        // behaviours is exactly the quiet wrong answer this engine refuses everywhere else. The recipe names both.
        FindDefaultProviders(receiver, memberName, args.Length, forProperty: false, out var provider, out var second);
        // Only contracts that do NOT extend one another are independent: an override in a derived interface has the
        // ancestor in its own chain, so it is the same member seen nearer, which is C#s most-specific rule. A
        // provider an ANCESTOR of another therefore drops out here, and what remains is the effective set.
        if (second != null)
        {
            RuntimeError($"'{memberName}/{args.Length}' has a DEFAULT in both '{provider}' and '{second}', and "
                         + $"'{ReportedTypeName(receiver)}' does not implement it. Add the member to its own type, "
                         + $"or name the contract you mean: {provider}.{memberName}(value) or "
                         + $"{second}.{memberName}(value).", line, column);
            return false;
        }
        if (provider != null)
        {
            result = InvokeInterfaceBody(ResolveInterface(provider)!.Defaults[key], receiver!, args, line, column,
                provider);
            return true;
        }

        // Then the STRUCTURAL reading, so `x is I` being True means I's members are reachable: a value that
        // matches an interface gets that interface's defaults, without having declared it. Declaration order is
        // what makes the choice deterministic when two interfaces provide the same member.
        if (receiver is ClassInstance or StructInstance)
        {
            foreach (var name in _interfaceOrder)
            {
                if (!_interfaces[name].Defaults.TryGetValue(key, out var body)) continue;
                if (!MatchesDeclaredInterface(receiver, name)) continue;
                result = InvokeInterfaceBody(body, receiver, args, line, column, name);
                return true;
            }
        }
        return false;
    }

    /// <summary>Runs an interface member's body with the receiver as `this`, through the member invocation path the
    /// extension registry already uses: a default method IS an ordinary body that needs a receiver.</summary>
    private object? InvokeInterfaceBody(FuncDecl body, object receiver, object?[] args, int line, int column,
        string? contract = null)
    {
        var previousThis = _currentThis;
        var previousClass = _currentClass;
        var previousReceiverName = _currentReceiverName;
        var previousContract = _currentContract;
        _currentContract = contract;
        _currentThis = receiver;
        // A default body resolves the receiver's own members by bare name. A struct has no ClassInfo, and its
        // members are reached through the instance's own declaration, so the class context stays empty for one.
        _currentClass = receiver is ClassInstance classInstance ? classInstance.Class : null;
        _currentReceiverName = null;
        try { return InvokeExtensionMember(body, args, line, column, receiver: receiver); }
        finally
        {
            _currentThis = previousThis;
            _currentClass = previousClass;
            _currentReceiverName = previousReceiverName;
            _currentContract = previousContract;
        }
    }
    /// <summary>The EXPLICIT implementation of a contract member on this value, or null. An explicit member is
    /// qualified with the contract it serves (see FuncDecl.Qualifier), and it is what makes two contracts able to
    /// demand one name with two different bodies.</summary>
    private FuncDecl? FindExplicitMember(object? receiver, string interfaceName, string memberName, int arity)
    {
        if (receiver is ClassInstance classInstance)
        {
            for (var current = classInstance.Class; current != null; current = current.Parent)
                foreach (var member in current.ExplicitMembers)
                    if (!member.IsStatic && member.Name.Equals(memberName, Comparison)
                        && member.Params.Count == arity && member.Qualifier != null
                        && member.Qualifier.Equals(interfaceName, Comparison))
                        return member;
            return null;
        }
        if (receiver is StructInstance structInstance
            && Structs.TryGetValue(structInstance.StructName, out var declaration))
        {
            foreach (var member in declaration.ExplicitMembers)
                if (!member.IsStatic && member.Name.Equals(memberName, Comparison)
                    && member.Params.Count == arity && member.Qualifier != null
                    && member.Qualifier.Equals(interfaceName, Comparison))
                    return member;
        }
        return null;
    }

    /// <summary>Runs `IShape.area(value, ...)`: the contract is named at the CALL SITE, because dispatch is by
    /// name and the receiver's type cannot differ per contract. The receiver arrives as the first argument, and the
    /// member is the value's explicit implementation, or the interface's own default for that name and arity.</summary>
    private object? InvokeQualifiedInterfaceMember(string interfaceName, string memberName,
        IReadOnlyList<Expr> argExprs, int line, int column)
    {
        var arity = argExprs.Count - 1;
        if (arity < 0)
        {
            RuntimeError($"'{interfaceName}.{memberName}' wants the value to call it on: write "
                         + $"`{interfaceName}.{memberName}(value, ...)`.", line, column);
            return null;
        }
        var receiver = Evaluate(argExprs[0]);
        if (ReferenceEquals(receiver, YieldSentinel)) return YieldSentinel;
        var args = new object?[arity];
        for (var index = 0; index < arity; index++)
        {
            args[index] = Evaluate(argExprs[index + 1]);
            if (ReferenceEquals(args[index], YieldSentinel)) return YieldSentinel;
        }
        if (!_interfaces.TryGetValue(interfaceName, out var info)) return null;

        var explicitMember = FindExplicitMember(receiver, interfaceName, memberName, arity);
        if (explicitMember != null) return InvokeInterfaceBody(explicitMember, receiver!, args, line, column, interfaceName);
        if (info.Defaults.TryGetValue(memberName + "/" + arity, out var defaultBody))
            return InvokeInterfaceBody(defaultBody, receiver!, args, line, column, interfaceName);

        RuntimeError($"'{interfaceName}' has no member '{memberName}/{arity}' that "
                     + $"'{ReportedTypeName(receiver)}' implements EXPLICITLY. Write `{interfaceName}.{memberName}"
                     + $"(value, ...)` to reach an explicit implementation, and `value.{memberName}(...)` for a "
                     + "member the value itself owns.", line, column);
        return null;
    }

    /// <summary>The spellings a bare call must use instead, when the member it could not find is an EXPLICIT
    /// implementation. Null when the class or struct has none of that name and arity, so the ordinary refusal is
    /// the right one and this stays out of its way.</summary>
    private string? ExplicitContractRecipe(object? receiver, string memberName, int arity)
    {
        var spellings = new List<string>();
        if (receiver is ClassInstance classInstance)
        {
            for (var current = classInstance.Class; current != null; current = current.Parent)
                foreach (var member in current.ExplicitMembers)
                    if (member.Name.Equals(memberName, Comparison) && member.Params.Count == arity
                        && member.Qualifier != null)
                        spellings.Add($"{member.Qualifier}.{memberName}(value)");
        }
        else if (receiver is StructInstance structInstance
                 && Structs.TryGetValue(structInstance.StructName, out var declaration))
        {
            foreach (var member in declaration.ExplicitMembers)
                if (member.Name.Equals(memberName, Comparison) && member.Params.Count == arity
                    && member.Qualifier != null)
                    spellings.Add($"{member.Qualifier}.{memberName}(value)");
        }
        return spellings.Count == 0 ? null : string.Join(" or ", spellings);
    }    /// <summary>The contract a qualified call names: `IShape.area(value)` targets the type reference `IShape` (the
         /// parser builds a bare type name as one), so the member access over it is what says "through this contract".
         /// A class or struct of the same name keeps the ordinary static-member path, so nothing is shadowed.</summary>
    private bool IsDeclaredInterfaceName(string name)
        => name.Length > 0
           && _interfaces.ContainsKey(name)
           && !Classes.ContainsKey(name)
           && !Structs.ContainsKey(name);

    /// <summary>True when a value of this class provides a member of that NAME with the shape a property demand
    /// asks for. A property is reached through its name here, so a FIELD answers a readable demand (and a writable
    /// one, since only a readonly field could not), and a script property answers through its own accessors. An
    /// inherited member counts, exactly as it does for a method.</summary>
    private bool ProvidesProperty(ClassInfo classInfo, string memberName, bool needsGetter, bool needsSetter)
    {
        for (var current = classInfo; current != null; current = current.Parent)
            if (ProvidesProperty(current.Fields, current.Properties, memberName, needsGetter, needsSetter))
                return true;
        return false;
    }

    /// <summary>One implementation for both kinds of value: a class walks its base chain and hands each level in,
    /// and a struct hands itself in once.</summary>
    private static bool ProvidesProperty(IEnumerable<FieldDecl> fields, IEnumerable<PropertyDecl> properties,
        string memberName, bool needsGetter, bool needsSetter)
    {
        foreach (var field in fields)
            if (!field.IsStatic && field.Name.Equals(memberName, StringComparison.OrdinalIgnoreCase)
                && (!needsSetter || true))
                return true;
        foreach (var property in properties)
            if (!property.IsStatic && property.Name.Equals(memberName, StringComparison.OrdinalIgnoreCase)
                && (!needsGetter || property.Getter != null || property.Initializer != null)
                && (!needsSetter || property.Setter != null))
                return true;
        return false;
    }

    /// <summary>The contracts reachable from this value whose definition answers a question the caller passes in:
    /// the EFFECTIVE set, which drops a contract that another provider extends, because an override is the same
    /// member seen nearer rather than an ambiguity. A value that only MATCHES a contract is not here: it is not a
    /// declared promise, and the structural fallback is asked separately.</summary>
    /// <summary>The ONE contract a value has that answers a member default, plus a SECOND independent one when the
    /// call is ambiguous, both by name. A single walk with two locals on purpose: this sits on the member-MISS path,
    /// where a capturing lambda, a List and a LINQ chain were measured at TENS OF KILOBYTES per call, so a call
    /// served by a default cost many times a plain one. Two contracts that extend one another are NOT independent,
    /// because an override is the same member seen nearer, which is the most-specific rule; only a provider that is
    /// not an ancestor of the other sets `second`.</summary>
    private void FindDefaultProviders(object? receiver, string memberName, int arity, bool forProperty,
        out string? provider, out string? second)
    {
        provider = null;
        second = null;
        IReadOnlyList<string> declaredNames = receiver switch
        {
            ClassInstance classInstance => classInstance.Class.ImplementedInterfaces,
            StructInstance structInstance when Structs.TryGetValue(structInstance.StructName, out var declaration)
                => declaration.ImplementedInterfaces,
            _ => []
        };
        var key = forProperty ? null : memberName + "/" + arity;
        foreach (var name in declaredNames)
        {
            if (!TryResolveInterface(name, out var info) || info == null) continue;
            if (forProperty ? FindPropertyGetter(info, memberName) == null : !info.Defaults.ContainsKey(key!)) continue;
            if (provider == null) { provider = name; continue; }
            if (!Extends(provider, name) && !Extends(name, provider)) second = name;
        }
    }

    /// <summary>True when `ancestor` is in `descendant`'s flattened chain: the same member reached from two places
    /// rather than an ambiguity. No LINQ and no allocation either, on the same hot path.</summary>
    private bool Extends(string ancestor, string descendant)
        => TryResolveInterface(descendant, out var info) && info != null
           && info.Flattened.Contains(ancestor, _comparer);

    /// <summary>The accessor a property default is served by, or null. A loop rather than First(), and the ONE place
    /// that decides what "this contract carries a property default" means.</summary>
    private FuncDecl? FindPropertyGetter(InterfaceInfo info, string propertyName)
    {
        foreach (var property in info.Properties)
            if (property.Getter != null && property.Name.Equals(propertyName, Comparison)) return property.Getter;
        return null;
    }

    /// <summary>The DEFAULT a value inherits for a property it does not provide, or false when no contract it
    /// declares gives one. Called from the property read paths, so a real member has already had its chance.</summary>
    private bool TryGetInterfacePropertyDefault(object? receiver, string propertyName, int line, int column,
        out object? value)
    {
        value = null;
        if (receiver is not (ClassInstance or StructInstance)) return false;
        FindDefaultProviders(receiver, propertyName, 0, forProperty: true, out var provider, out var second);
        if (second != null)
        {
            RuntimeError($"Property '{propertyName}' has a DEFAULT body in both '{provider}' and '{second}', and "
                         + $"'{ReportedTypeName(receiver)}' does not provide it. Add a field or a property named "
                         + $"'{propertyName}' to its own type, or name the contract you mean: "
                         + $"{provider}.{propertyName}(value) or {second}.{propertyName}(value).", line, column);
            return false;
        }
        if (provider != null)
        {
            var getter = FindPropertyGetter(ResolveInterface(provider)!, propertyName);
            value = InvokeInterfaceBody(getter!, receiver!, [], line, column, provider);
            return true;
        }
        // A contract this value MATCHES structurally supplies its default too, which is what makes `is` and the
        // members of a contract agree (the same rule the method defaults follow).
        foreach (var name in _interfaceOrder)
        {
            if (!TryResolveInterface(name, out var info) || info == null) continue;
            var signature = FindByName(info.Properties, propertyName, static p => p.Name,
                static p => p.Getter != null);
            if (signature == null || !MatchesDeclaredInterface(receiver, name)) continue;
            value = InvokeInterfaceBody(signature.Getter!, receiver!, [], line, column, name);
            return true;
        }
        return false;
    }    /// <summary>The contract a script SPELLS, resolved to what the engine matches on. A non-generic one is a
         /// dictionary hit; an instantiation (`I<int>`) is registered on demand under that spelling, so the `is` test and
         /// the `implements` clause that created it compare the SAME key. Null when nothing is declared under that name,
         /// with a loud refusal for a template named WITHOUT its arguments. Nothing here calls its tolerant twin: a
         /// mutual pair is how a stack overflow is written.</summary>
    private InterfaceInfo? ResolveInterface(string spelling)
    {
        if (_interfaces.TryGetValue(spelling, out var concrete)) return concrete;
        if (!spelling.Contains('<') && _genericInterfaceTemplates.TryGetValue(spelling, out var template))
            throw new ScriptException(
                $"'{spelling}' is a GENERIC interface, so write its arguments: "
                + $"`{spelling}<{string.Join(", ", template.TypeParameters)}>`.");
        return InstantiateInterface(spelling);
    }

    /// <summary>The tolerant form, for a question whose answer may simply be no.</summary>
    private bool TryResolveInterface(string spelling, out InterfaceInfo? info)
    {
        if (_interfaces.TryGetValue(spelling, out info)) return true;
        info = spelling.Contains('<') ? InstantiateInterface(spelling) : null;
        return info != null;
    }

    /// <summary>Builds (and caches) the closed form of a generic contract, or null when the name before the
    /// arguments is not a declared template. Its flattened chain starts with the SPELLING, which is what a class
    /// records and `is` asks for, so the arguments travel with the contract.</summary>
    private InterfaceInfo? InstantiateInterface(string spelling)
    {
        var lt = spelling.IndexOf('<');
        var baseName = lt > 0 ? spelling[..lt] : "";
        var known = baseName.Length > 0 && _genericInterfaceTemplates.ContainsKey(baseName);
        if (!known) return null;
        var open = _genericInterfaceTemplates[baseName];
        var arguments = spelling[(lt + 1)..].TrimEnd('>').Split(',').Select(a => a.Trim()).ToList();
        if (arguments.Count != open.TypeParameters.Count)
            throw new ScriptException(
                $"'{baseName}' takes {open.TypeParameters.Count} type argument(s), and '{spelling}' gives "
                + $"{arguments.Count}.");
        if (arguments.Any(a => open.TypeParameters.Contains(a, _comparer)))
            throw new ScriptException(
                $"'{spelling}' passes a TYPE PARAMETER as an argument, which would need substitution: write "
                + "concrete arguments, or implement the non-generic contract.");
        var flattened = new List<string> { spelling };
        foreach (var inherited in open.Flattened.Skip(1))
            if (!flattened.Contains(inherited, _comparer)) flattened.Add(inherited);
        var info = new InterfaceInfo(open.Name, open.Parents, flattened, open.Members, open.Defaults, open.Line,
            open.Column)
        {
            Required = open.Required,
            Properties = open.Properties,
            RequiredProperties = open.RequiredProperties,
            TypeParameters = open.TypeParameters,
            TypeArguments = arguments,
            IsGenericInstantiation = true
        };
        _interfaces[spelling] = info;
        return info;
    }
}