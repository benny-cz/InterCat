using System.Reflection;
using System.Reflection.Emit;

namespace InterCat.CaptureBroker.Tests;

/// <summary>
/// Every method the capture callback can run, found by walking the IL from where ETW's callback enters InterCat: the
/// calls each makes, an interface call followed into every implementation the broker is built with, and what each method
/// allocates or reaches outside InterCat (R9, P12, §9.3). An instruction that only builds the exception a throw raises
/// runs when the callback is already failing, so what it allocates is not the callback's steady cost and is set apart.
/// </summary>
internal sealed class CallbackReach
{
    private static readonly OpCode[] OneByte = new OpCode[0x100];
    private static readonly OpCode[] TwoByte = new OpCode[0x100];

    private readonly Assembly[] scope;
    private readonly HashSet<MethodBase> visited = [];

    static CallbackReach()
    {
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var code = (OpCode)field.GetValue(null)!;
            ushort value = unchecked((ushort)code.Value);
            if (code.Size == 1)
            {
                OneByte[value] = code;
            }
            else
            {
                TwoByte[value & 0xFF] = code;
            }
        }
    }

    private CallbackReach(Assembly[] scope) => this.scope = scope;

    /// <summary>Each method reached, by its type and name.</summary>
    public SortedSet<string> Reached { get; } = new(StringComparer.Ordinal);

    /// <summary>Each call to something the callback must never reach: the method, the target and why.</summary>
    public SortedSet<string> Forbidden { get; } = new(StringComparer.Ordinal);

    /// <summary>Each allocation outside a throw: the method and what it allocates.</summary>
    public SortedSet<string> Allocations { get; } = new(StringComparer.Ordinal);

    /// <summary>Each lock InterCat's own code takes: the method and the kind of lock.</summary>
    public SortedSet<string> Locks { get; } = new(StringComparer.Ordinal);

    /// <summary>Walks every method reachable from <paramref name="roots"/> that one of <paramref name="scope"/> declares.</summary>
    public static CallbackReach From(IEnumerable<MethodBase> roots, params Assembly[] scope)
    {
        var walk = new CallbackReach(scope);
        foreach (MethodBase root in roots)
        {
            walk.Visit(root);
        }

        return walk;
    }

    public static string Name(MethodBase method) => $"{TypeName(method.DeclaringType!)}.{method.Name}";

    /// <summary>
    /// Every call the methods of <paramref name="assemblies"/> make, each with the method it is written in, so a call an
    /// async method or a lambda makes is named for the method that holds it rather than for a compiler-generated one.
    /// </summary>
    public static IEnumerable<(string Caller, MethodBase Target)> Calls(params Assembly[] assemblies)
    {
        const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
            | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (Type type in assemblies.SelectMany(Types))
        {
            foreach (MethodBase method in type.GetMethods(Declared).Concat<MethodBase>(type.GetConstructors(Declared)))
            {
                if (method.GetMethodBody()?.GetILAsByteArray() is not { } il)
                {
                    continue;
                }

                Type[]? typeArguments = type.IsGenericType ? type.GetGenericArguments() : null;
                Type[]? methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
                foreach (Instruction instruction in Decode(il))
                {
                    if (instruction.Code.OperandType == OperandType.InlineMethod
                        && method.Module.ResolveMethod((int)instruction.Operand, typeArguments, methodArguments) is { } target)
                    {
                        yield return (WrittenIn(method), target);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The method a body was written in: a state machine's, a closure's, a lambda's or a local function's is named for
    /// the method that holds it.
    /// </summary>
    private static string WrittenIn(MethodBase method)
    {
        string name = method.Name;
        if (name.StartsWith('<') && name.IndexOf('>') > 1)
        {
            name = name[1..name.IndexOf('>')];
        }

        Type type = method.DeclaringType!;
        while (type.IsNested && type.Name.StartsWith('<'))
        {
            int close = type.Name.IndexOf('>');
            if (close > 1)
            {
                name = type.Name[1..close];
            }

            type = type.DeclaringType!;
        }

        return $"{TypeName(type)}.{name}";
    }

    private static string TypeName(Type type) => (type.FullName ?? type.Name).Split('`')[0];

    private void Visit(MethodBase method)
    {
        if (!visited.Add(method))
        {
            return;
        }

        Reached.Add(Name(method));
        if (method.GetMethodBody() is not { } body || body.GetILAsByteArray() is not { } il)
        {
            return;
        }

        List<Instruction> code = Decode(il);
        HashSet<int> raising = ThrowPaths(code, body);
        Type[]? typeArguments = method.DeclaringType is { IsGenericType: true } generic ? generic.GetGenericArguments() : null;
        Type[]? methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        foreach (Instruction instruction in code)
        {
            bool raised = raising.Contains(instruction.Offset);
            short value = instruction.Code.Value;
            if (value == OpCodes.Newarr.Value || value == OpCodes.Box.Value)
            {
                if (!raised)
                {
                    Type type = method.Module.ResolveType((int)instruction.Operand, typeArguments, methodArguments);
                    Allocations.Add(value == OpCodes.Newarr.Value
                        ? $"{Name(method)}: new {TypeName(type)}[]"
                        : $"{Name(method)}: box {TypeName(type)}");
                }

                continue;
            }

            if (instruction.Code.OperandType != OperandType.InlineMethod)
            {
                continue;
            }

            MethodBase target = method.Module.ResolveMethod((int)instruction.Operand, typeArguments, methodArguments)!;
            if (Prohibited(target) is { } why)
            {
                Forbidden.Add($"{Name(method)} -> {Name(target)}: {why}");
                continue;
            }

            if (!raised && Allocates(target, value == OpCodes.Newobj.Value) is { } allocated)
            {
                Allocations.Add($"{Name(method)}: {allocated}");
            }

            if (Takes(target) is { } taken)
            {
                Locks.Add($"{Name(method)}: {taken}");
            }

            if (Array.IndexOf(scope, target.DeclaringType!.Assembly) >= 0)
            {
                foreach (MethodBase dispatched in Dispatch(target))
                {
                    Visit(dispatched);
                }
            }
        }
    }

    /// <summary>
    /// What a call can run: an interface method's every implementation the scope declares, and its default body; a virtual
    /// method's every override; anything else, itself.
    /// </summary>
    private IEnumerable<MethodBase> Dispatch(MethodBase target)
    {
        Type declaring = target.DeclaringType!;
        if (declaring.IsInterface)
        {
            if (!target.IsAbstract)
            {
                yield return target;
            }

            foreach (Type type in scope.SelectMany(Types).Where(type => !type.IsInterface && declaring.IsAssignableFrom(type)))
            {
                InterfaceMapping map = type.GetInterfaceMap(declaring);
                int index = Array.FindIndex(map.InterfaceMethods,
                    method => method.MetadataToken == target.MetadataToken && method.Module == target.Module);
                if (index >= 0 && map.TargetMethods[index].DeclaringType == type)
                {
                    yield return map.TargetMethods[index];
                }
            }

            yield break;
        }

        yield return target;
        if (target is MethodInfo { IsVirtual: true, IsFinal: false } virtualMethod && !declaring.IsSealed)
        {
            MethodInfo definition = virtualMethod.GetBaseDefinition();
            foreach (MethodInfo candidate in scope.SelectMany(Types)
                .Where(type => type != declaring && declaring.IsAssignableFrom(type))
                .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly))
                .Where(method => method.GetBaseDefinition() == definition))
            {
                yield return candidate;
            }
        }
    }

    /// <summary>Every type an assembly declares, compiler-generated ones among them.</summary>
    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }

    /// <summary>Why the callback must never make this call, or null when it may.</summary>
    private static string? Prohibited(MethodBase target)
    {
        Type type = target.DeclaringType!;
        string space = type.Namespace ?? string.Empty;
        string name = target.Name;
        string full = TypeName(type);
        return space switch
        {
            _ when space.StartsWith("InterCat.Storage", StringComparison.Ordinal) => "the store, which a callback never writes or reads",
            _ when space.StartsWith("InterCat.Capture.Journal", StringComparison.Ordinal) => "the journal, which the drain writes",
            _ when space.StartsWith("InterCat.Analysis", StringComparison.Ordinal)
                || space.StartsWith("InterCat.Application", StringComparison.Ordinal) => "a query",
            _ when space.StartsWith("System.IO", StringComparison.Ordinal) => "file or stream I/O",
            _ when space.StartsWith("System.Net", StringComparison.Ordinal) => "the network or a name lookup",
            _ when space.StartsWith("Microsoft.Diagnostics.Symbols", StringComparison.Ordinal) => "a symbol lookup",
            _ when space.StartsWith("System.Security.Principal", StringComparison.Ordinal) => "an account name lookup",
            _ when space.StartsWith("System.Reflection", StringComparison.Ordinal) => "reflection",
            _ when full is "System.Diagnostics.Process" => "a query of another process",
            _ when full is "System.Console" or "System.Diagnostics.Trace" => "console or trace output",
            _ when Waits(full, name) => "a wait",
            _ when typeof(Microsoft.Diagnostics.Tracing.TraceEvent).IsAssignableFrom(type) && Decodes(name) =>
                "decoding beyond a descriptor and shape check",
            _ => null,
        };
    }

    private static bool Waits(string type, string name) => (type, name) switch
    {
        ("System.Threading.Thread", "Sleep" or "Join") => true,
        ("System.Threading.Tasks.Task", "Wait" or "WaitAll" or "WaitAny" or "get_Result") => true,
        ("System.Runtime.CompilerServices.TaskAwaiter" or "System.Runtime.CompilerServices.ValueTaskAwaiter"
            or "System.Runtime.CompilerServices.ConfiguredTaskAwaitable+ConfiguredTaskAwaiter", "GetResult") => true,
        ("System.Threading.WaitHandle", "WaitOne" or "WaitAll" or "WaitAny") => true,
        ("System.Threading.Monitor", "Wait") => true,
        ("System.Threading.SemaphoreSlim" or "System.Threading.ManualResetEventSlim" or "System.Threading.CountdownEvent",
            "Wait") => true,
        ("System.Threading.Barrier", "SignalAndWait") => true,
        ("System.Threading.Channels.ChannelWriter", "WriteAsync" or "WaitToWriteAsync") => true,
        _ => false,
    };

    private static bool Decodes(string name) =>
        name.StartsWith("Payload", StringComparison.Ordinal) || name.StartsWith("get_Payload", StringComparison.Ordinal)
        || name is "get_ProviderName" or "get_EventName" or "get_TaskName" or "get_OpcodeName" or "get_FormattedMessage"
            or "GetFormattedMessage" or "get_ProcessName" or "Dump" or "ToString" or "Clone";

    /// <summary>The kind of lock a call takes, or null when it takes none.</summary>
    private static string? Takes(MethodBase target) => (TypeName(target.DeclaringType!), target.Name) switch
    {
        ("System.Threading.Monitor", "Enter" or "TryEnter" or "ReliableEnter") => "Monitor",
        ("System.Threading.Lock", "Enter" or "EnterScope" or "TryEnter") => "Lock",
        ("System.Threading.SpinLock", "Enter" or "TryEnter") => "SpinLock",
        ("System.Threading.ReaderWriterLockSlim", _) when target.Name.StartsWith("Enter", StringComparison.Ordinal)
            || target.Name.StartsWith("TryEnter", StringComparison.Ordinal) => "ReaderWriterLockSlim",
        _ => null,
    };

    /// <summary>What a call allocates on the heap, or null when it allocates nothing the callback owns.</summary>
    private static string? Allocates(MethodBase target, bool constructs)
    {
        Type type = target.DeclaringType!;
        string full = TypeName(type);
        string name = target.Name;
        if (constructs)
        {
            return type.IsValueType ? null : $"new {full}";
        }

        return (full, name) switch
        {
            ("System.String", "Concat" or "Format" or "Join" or "Create" or "Substring" or "Replace" or "Insert"
                or "Remove" or "PadLeft" or "PadRight" or "ToUpper" or "ToLower" or "ToUpperInvariant" or "ToLowerInvariant"
                or "Trim" or "TrimStart" or "TrimEnd" or "Split" or "Normalize") => $"{full}.{name}",
            ("System.Runtime.CompilerServices.DefaultInterpolatedStringHandler", "ToStringAndClear") => $"{full}.{name}",
            ("System.Array", "Resize") or ("System.GC", "AllocateArray" or "AllocateUninitializedArray") => $"{full}.{name}",
            ("System.Runtime.InteropServices.Marshal", _) when name.StartsWith("PtrToString", StringComparison.Ordinal)
                || name.StartsWith("StringTo", StringComparison.Ordinal)
                || name is "AllocHGlobal" or "AllocCoTaskMem" => $"{full}.{name}",
            _ when (type.Namespace ?? string.Empty).StartsWith("System.Linq", StringComparison.Ordinal) => $"{full}.{name}",
            _ when name is "ToString" or "ToArray" or "ToList" => $"{full}.{name}",
            _ => null,
        };
    }

    /// <summary>The offsets of every instruction in a block that ends by throwing: what builds the exception it raises.</summary>
    private static HashSet<int> ThrowPaths(List<Instruction> code, MethodBody body)
    {
        var starts = new HashSet<int> { 0 };
        foreach (Instruction instruction in code)
        {
            starts.UnionWith(instruction.Targets);
            if (instruction.Code.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch or FlowControl.Return
                or FlowControl.Throw)
            {
                starts.Add(instruction.Next);
            }
        }

        foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
        {
            starts.Add(clause.TryOffset);
            starts.Add(clause.HandlerOffset);
            if (clause.Flags == ExceptionHandlingClauseOptions.Filter)
            {
                starts.Add(clause.FilterOffset);
            }
        }

        var raising = new HashSet<int>();
        for (int index = 0; index < code.Count; index++)
        {
            if (code[index].Code.Value != OpCodes.Throw.Value)
            {
                continue;
            }

            for (int back = index; back >= 0; back--)
            {
                raising.Add(code[back].Offset);
                if (starts.Contains(code[back].Offset))
                {
                    break;
                }
            }
        }

        return raising;
    }

    private static List<Instruction> Decode(byte[] il)
    {
        var code = new List<Instruction>();
        int position = 0;
        while (position < il.Length)
        {
            int offset = position;
            OpCode opcode = il[position] == 0xFE ? TwoByte[il[position + 1]] : OneByte[il[position]];
            position += opcode.Size;
            long operand = 0;
            int[] targets = [];
            switch (opcode.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                    operand = (sbyte)il[position];
                    position += 1;
                    targets = [position + (int)operand];
                    break;
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    operand = il[position];
                    position += 1;
                    break;
                case OperandType.InlineVar:
                    operand = BitConverter.ToUInt16(il, position);
                    position += 2;
                    break;
                case OperandType.InlineBrTarget:
                    operand = BitConverter.ToInt32(il, position);
                    position += 4;
                    targets = [position + (int)operand];
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    operand = BitConverter.ToInt64(il, position);
                    position += 8;
                    break;
                case OperandType.InlineSwitch:
                    int count = BitConverter.ToInt32(il, position);
                    position += 4;
                    int end = position + (4 * count);
                    targets = new int[count];
                    for (int index = 0; index < count; index++)
                    {
                        targets[index] = end + BitConverter.ToInt32(il, position + (4 * index));
                    }

                    position = end;
                    break;
                default:
                    operand = BitConverter.ToInt32(il, position);
                    position += 4;
                    break;
            }

            code.Add(new(offset, opcode, position, operand, targets));
        }

        return code;
    }

    private readonly record struct Instruction(int Offset, OpCode Code, int Next, long Operand, int[] Targets);
}
