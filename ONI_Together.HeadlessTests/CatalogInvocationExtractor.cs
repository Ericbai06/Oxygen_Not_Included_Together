using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ONI_Together.HeadlessTests;

internal static class CatalogInvocationExtractor
{
    private static readonly HashSet<string> StateMethods =
    [
        "StartSM",
        "GoTo",
        "IsInsideState",
        "Get",
        "Set",
        "Delta",
        "Trigger"
    ];

    public static void Extract(
        CatalogExtractionContext context,
        SyntaxTree tree)
    {
        SyntaxNode root = tree.GetRoot();
        foreach (AssignmentExpressionSyntax assignment in root.DescendantNodes()
                     .OfType<AssignmentExpressionSyntax>())
            AddEventAssignment(context, assignment);

        foreach (InvocationExpressionSyntax invocation in root.DescendantNodes()
                     .OfType<InvocationExpressionSyntax>())
            AddInvocation(context, invocation);
    }

    private static void AddInvocation(
        CatalogExtractionContext context,
        InvocationExpressionSyntax invocation)
    {
        IMethodSymbol? method = CatalogSymbolHelpers.InvocationMethod(
            context.Model, invocation);
        if (method is null)
            return;
        if (IsDynamicHarmony(method))
        {
            AddDynamicHarmony(context, invocation);
            return;
        }
        SyncEntryKind? packetKind = PacketKind(context.Model, invocation, method);
        if (packetKind is not null)
        {
            AddMethodCall(context, invocation, new CatalogCall(packetKind.Value, method));
            return;
        }
        if (IsGameMethod(method, "Subscribe"))
        {
            AddMethodCall(context, invocation,
                new CatalogCall(SyncEntryKind.EventSubscribe, method));
            return;
        }
        if (method.Name == "Trigger" && !IsStateMachineMethod(method))
        {
            AddMethodCall(context, invocation,
                new CatalogCall(SyncEntryKind.EventPublish, method));
            return;
        }
        if (method.Name == "StartCoroutine")
        {
            AddCoroutine(context, invocation);
            return;
        }
        if (IsStateMachineMethod(method))
            AddMethodCall(context, invocation,
                new CatalogCall(SyncEntryKind.StateMachine, method));
    }

    private static void AddEventAssignment(
        CatalogExtractionContext context,
        AssignmentExpressionSyntax assignment)
    {
        if (!assignment.IsKind(SyntaxKind.AddAssignmentExpression) &&
            !assignment.IsKind(SyntaxKind.SubtractAssignmentExpression))
            return;
        if (context.Model.GetSymbolInfo(assignment.Left).Symbol is not IEventSymbol eventSymbol)
            return;
        IMethodSymbol? handler = context.Model.GetSymbolInfo(assignment.Right).Symbol
            as IMethodSymbol;
        string operation = assignment.IsKind(SyntaxKind.AddAssignmentExpression)
            ? "add"
            : "remove";
        string eventName = eventSymbol.ToDisplayString(
            SymbolDisplayFormat.CSharpErrorMessageFormat);
        string handlerName = handler is null
            ? SyncCatalogSourceScanner.Normalize(assignment.Right.ToString())
            : CatalogSymbolHelpers.MethodSignature(handler);
        AddCandidate(context,
            SyncEntryKind.EventSubscribe,
            CatalogSymbolHelpers.EnclosingMethod(context.Model, assignment),
            $"{operation} {eventName} -> {handlerName}",
            $"{eventName} {(operation == "add" ? "+=" : "-=")} {handlerName}",
            assignment);
    }

    private static bool IsDynamicHarmony(IMethodSymbol method)
    {
        return method.Name == "Patch" && method.ContainingType.Name == "Harmony";
    }

    private static void AddDynamicHarmony(
        CatalogExtractionContext context,
        InvocationExpressionSyntax invocation)
    {
        DynamicTarget target = ReadDynamicTarget(context.Model, invocation);
        AddCandidate(context,
            SyncEntryKind.HarmonyPatch,
            CatalogSymbolHelpers.EnclosingMethod(context.Model, invocation),
            target.Signature,
            $"Harmony.Patch:{target.Display}",
            invocation);
    }

    private static DynamicTarget ReadDynamicTarget(
        SemanticModel model,
        InvocationExpressionSyntax patchInvocation)
    {
        ExpressionSyntax? original = patchInvocation.ArgumentList.Arguments
            .FirstOrDefault()?.Expression;
        if (original is null)
            return new DynamicTarget("", "<missing>");
        CatalogHarmonyTarget target = CatalogHarmonyTargetResolver.ResolveExpression(
            model, original);
        string display = target.Display.Length > 0
            ? target.Display
            : SyncCatalogSourceScanner.Normalize(original.ToString());
        return new DynamicTarget(target.Signature, display);
    }

    private static SyncEntryKind? PacketKind(
        SemanticModel model,
        InvocationExpressionSyntax invocation,
        IMethodSymbol method)
    {
        bool packetRegistration = IsPacketRegistration(method);
        bool packetArgument = invocation.ArgumentList.Arguments.Any(argument =>
            CatalogSymbolHelpers.IsPacketType(model.GetTypeInfo(argument.Expression).Type));
        bool packetCarrierArgument = invocation.ArgumentList.Arguments.Any(argument =>
            CatalogSymbolHelpers.ContainsPacketType(
                model.GetTypeInfo(argument.Expression).Type));
        if (packetRegistration)
            return SyncEntryKind.PacketRegistration;
        if (method.Name == "Deserialize" &&
            CatalogSymbolHelpers.IsPacketType(method.ContainingType))
            return SyncEntryKind.PacketDeserialize;
        if (packetCarrierArgument && IsRelayMethod(method))
            return SyncEntryKind.PacketRelay;
        if (packetArgument && method.Name.Contains("Send", StringComparison.Ordinal))
            return SyncEntryKind.PacketSend;
        if ((packetCarrierArgument && method.Name.Contains("Dispatch", StringComparison.Ordinal)) ||
            method.Name == "OnDispatched")
            return SyncEntryKind.PacketDispatch;
        return null;
    }

    private static bool IsPacketRegistration(IMethodSymbol method)
    {
        if (method.Name == "RegisterDefaults")
            return method.ContainingType.Name == "PacketRegistry";
        if (method.Name == "AutoRegisterPackets")
            return method.ContainingType.Name == "PacketRegistrationHelper";
        if (method.Name is not ("Register" or "TryRegister"))
            return false;
        return method.ContainingType.Name.Contains("Packet", StringComparison.Ordinal) ||
            method.TypeArguments.Any(CatalogSymbolHelpers.IsPacketType) ||
            method.Parameters.Any(parameter =>
                CatalogSymbolHelpers.IsPacketType(parameter.Type));
    }

    private static bool IsRelayMethod(IMethodSymbol method)
    {
        return method.Name.Contains("Envelope", StringComparison.Ordinal) ||
            method.Name.Contains("Relay", StringComparison.Ordinal) ||
            method.ContainingType.Name.Contains("Relay", StringComparison.Ordinal);
    }

    private static bool IsGameMethod(IMethodSymbol method, string name)
    {
        return method.Name == name && method.ContainingType.Name == "Game";
    }

    private static bool IsStateMachineMethod(IMethodSymbol method)
    {
        return StateMethods.Contains(method.Name) &&
            (method.ContainingType.Name.Contains("StateMachine", StringComparison.Ordinal) ||
             method.ContainingType.Name.Contains("StatesInstance", StringComparison.Ordinal));
    }

    private static void AddMethodCall(
        CatalogExtractionContext context,
        InvocationExpressionSyntax invocation,
        CatalogCall call)
    {
        AddCandidate(context,
            call.Kind,
            CatalogSymbolHelpers.EnclosingMethod(context.Model, invocation),
            CatalogSymbolHelpers.MethodSignature(call.Method),
            SyncCatalogSourceScanner.Normalize(invocation.ToString()),
            invocation);
    }

    private static void AddCoroutine(
        CatalogExtractionContext context,
        InvocationExpressionSyntax invocation)
    {
        InvocationExpressionSyntax? iteratorCall = FindIteratorCall(
            context.Model, invocation);
        IMethodSymbol? iterator = iteratorCall is null
            ? null
            : CatalogSymbolHelpers.InvocationMethod(context.Model, iteratorCall);
        string target = iterator is null
            ? ""
            : $"{CatalogSymbolHelpers.TypeName(iterator.ReturnType)} " +
              CatalogSymbolHelpers.MethodSignature(iterator);
        string iteratorText = iteratorCall?.ToString() ?? "<missing>";
        AddCandidate(context,
            SyncEntryKind.Coroutine,
            CatalogSymbolHelpers.EnclosingMethod(context.Model, invocation),
            target,
            $"StartCoroutine -> {SyncCatalogSourceScanner.Normalize(iteratorText)}",
            invocation);
    }

    private static InvocationExpressionSyntax? FindIteratorCall(
        SemanticModel model,
        InvocationExpressionSyntax startCoroutine)
    {
        ExpressionSyntax? argument = startCoroutine.ArgumentList.Arguments
            .FirstOrDefault()?.Expression;
        if (argument is InvocationExpressionSyntax direct)
            return direct;
        ISymbol? valueSymbol = argument is null
            ? null
            : model.GetSymbolInfo(argument).Symbol;
        SyntaxNode? scope = startCoroutine.Ancestors()
            .FirstOrDefault(node => node is MethodDeclarationSyntax or
                AccessorDeclarationSyntax or LocalFunctionStatementSyntax);
        if (scope is null)
            return null;
        IEnumerable<(int Position, InvocationExpressionSyntax Call)> declarations =
            scope.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Where(declaration => declaration.SpanStart < startCoroutine.SpanStart &&
                    SymbolEqualityComparer.Default.Equals(
                        model.GetDeclaredSymbol(declaration), valueSymbol))
                .Select(declaration => declaration.Initializer?.Value)
                .OfType<InvocationExpressionSyntax>()
                .Select(call => (call.SpanStart, call));
        IEnumerable<(int Position, InvocationExpressionSyntax Call)> assignments =
            scope.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(assignment => assignment.SpanStart < startCoroutine.SpanStart &&
                SymbolEqualityComparer.Default.Equals(
                    model.GetSymbolInfo(assignment.Left).Symbol, valueSymbol))
            .Select(assignment => assignment.Right)
            .OfType<InvocationExpressionSyntax>()
            .Select(call => (call.SpanStart, call));
        return declarations.Concat(assignments)
            .OrderBy(item => item.Position)
            .LastOrDefault().Call;
    }

    private static void AddCandidate(
        CatalogExtractionContext context,
        SyncEntryKind kind,
        string symbol,
        string target,
        string bootstrap,
        SyntaxNode sourceNode)
    {
        string normalizedBootstrap = SyncCatalogSourceScanner.Normalize(bootstrap);
        string key = string.Join("\n", kind, symbol, target, normalizedBootstrap);
        context.CallsiteOccurrences.TryGetValue(key, out int previous);
        int occurrence = previous + 1;
        context.CallsiteOccurrences[key] = occurrence;
        context.Candidates.Add(new SyncEntryCandidate(
            kind,
            symbol,
            target,
            normalizedBootstrap,
            context.Variant,
            CatalogSourceClassification.ForNode(context, sourceNode),
            $"{normalizedBootstrap}#call:{occurrence:D4}"));
    }

    private sealed record DynamicTarget(string Signature, string Display);
    private sealed record CatalogCall(SyncEntryKind Kind, IMethodSymbol Method);
}
