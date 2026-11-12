namespace ScriptLangCore;

internal partial class Interpreter
{
    public sealed record ClassInfo(
        string Name,
        string? ParentName,
        IReadOnlyList<FieldDecl> Fields,
        IReadOnlyList<FuncDecl> Methods,
        IReadOnlyList<ConstructorDecl> Constructors,
        IReadOnlyList<ClassDeclStmt> NestedClasses,
        IReadOnlyList<EventDecl> Events,
        IReadOnlyDictionary<TokenType, FuncDecl> OperatorMethods,
        int LineNumber,
        AccessModifier Access = AccessModifier.Public,
        IReadOnlyList<PropertyDecl>? Properties = null
    )
    {
        public ClassInfo? Parent { get; init; }
        /// <summary>The interfaces this class (and its base chain) implements, FLATTENED: a name set, because an
        /// interface is nominal here, so `is`/`as`/a cast is one lookup rather than a shape test.</summary>
        public IReadOnlyList<string> ImplementedInterfaces { get; init; } = [];
        /// <summary>The members this class implements EXPLICITLY, one per contract (`IShape.area`). They are
        /// kept OUT of <see cref="Methods"/>, which is what makes the bare name refuse: a qualified member is
        /// reached through the contract it serves, with the receiver as the first argument.</summary>
        public IReadOnlyList<FuncDecl> ExplicitMembers { get; init; } = [];
        public FuncDecl? Destructor { get; init; }
        public Dictionary<string, object?>? StaticFields { get; init; }
        public List<FieldDecl>? StaticFieldDecls { get; init; }
        public List<FuncDecl>? StaticMethods { get; init; }
        public IReadOnlyList<PropertyDecl> Properties { get; init; } = Properties ?? [];
        public IReadOnlyList<StructDeclStmt> NestedStructs { get; init; } = [];
        /// <summary>True for record types, structural value equality (field-by-field ==/!=).</summary>
        public bool IsRecord { get; init; }
        /// <summary>The type arguments this class was instantiated with, when it came from a generic
        /// template; empty otherwise. The specialised fields carry the substituted types, but the
        /// ARGUMENTS themselves were previously lost - so a runtime value could not report or bind them.</summary>
        public IReadOnlyList<TypeInfo> TypeArguments { get; init; } = [];
        /// <summary>The CLR BASE this class extends, when it extends one instead of another script class. Only the
        /// bases the engine can bridge are accepted (see <see cref="ScriptTextBridge"/>), because a script class is a
        /// <see cref="ClassInstance"/> and cannot become a CLR subtype: the base is the FACE the instance is handed to the
        /// CLR as, and <see cref="ClassInstance.ClrFacade"/> holds that face once it exists.</summary>
        public Type? ClrBase { get; init; }
        public override string ToString() => $"Class {Name}";
    }

    public sealed record StructInstance(string StructName, Dictionary<string, object?> Fields, int Line)
    {
        public StructDeclStmt? Declaration { get; init; }
        public bool IsInInitPhase { get; set; }
        public override string ToString() => ValueFormatter.Format(this);
    }

    /// <summary>A member of a script `enum`: the value its declaration gave it, plus the enum it belongs to,
    /// so `typeof` and `is` can name it (`Color.Red is Color`, `Color.Red is Enum`) instead of reporting the
    /// bare long its underlying type is stored as. Every numeric path unwraps it (see UnwrapEnum), and
    /// ToString forwards to that value so printing a member still shows the number.</summary>
    internal sealed record EnumValue(string EnumName, long Value)
    {
        public override string ToString() => Value.ToString();
    }

    public sealed record ClassInstance(ClassInfo Class, Dictionary<string, object?> FieldValues, Dictionary<string, EventInfo> Events)
    {
        public bool IsInInitPhase { get; set; }
        /// <summary>The CLR face this instance is handed to the CLR as, created on first need when its class extends a
        /// bridged base (`class Sink extends TextWriter`) and cached so the same writer comes back every time.</summary>
        internal object? ClrFacade { get; set; }
        public override string ToString() => ValueFormatter.Format(this);
    }

    internal readonly record struct SuperProxy(object Instance, ClassInfo ParentClass);
    internal readonly record struct BaseProxy(object Instance, ClassInfo BaseClass);
    internal readonly record struct BoundMethod(object Instance, FuncDecl Method);
}
