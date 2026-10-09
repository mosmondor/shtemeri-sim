using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Shtemeri.Api;

namespace Shtemeri.Sim.Hosting;

/// <summary>A compiled fleet: a factory that creates one <see cref="Shtemer"/> instance per shtemer.</summary>
public sealed class FleetProgram
{
    public string Name { get; }
    public string SourcePath { get; }
    public Type ShtemerType { get; }
    private readonly Func<Shtemer>? _factory;

    public FleetProgram(string name, string sourcePath, Type type) { Name = name; SourcePath = sourcePath; ShtemerType = type; }

    /// <summary>A fleet made by a factory (e.g. a scripted fleet that replays recorded commands).</summary>
    public FleetProgram(string name, string sourcePath, Func<Shtemer> factory)
    { Name = name; SourcePath = sourcePath; ShtemerType = typeof(Shtemer); _factory = factory; }

    public Shtemer Create() => _factory != null ? _factory() : (Shtemer)Activator.CreateInstance(ShtemerType)!;
}

/// <summary>
/// Compiles a fleet's C# source with Roslyn against the season API, after instrumenting it for the instruction budget.
/// Each fleet gets its own in-memory assembly, so two fleets may use the same namespace and class name
/// (e.g. two versions of the same fleet). Compiled assemblies are cached on disk by source hash.
/// </summary>
public static class FleetCompiler
{
    private const string InstrumentationVersion = "budget-v2";
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Type> Loaded = new();
    private static ImmutableArray<MetadataReference> _refs;

    /// <summary>Directory for compiled assemblies; null disables the disk cache.</summary>
    public static string? CacheDir { get; set; } = Path.Combine(AppContext.BaseDirectory, ".fleet-cache");

    /// <summary>Scale applied to the per-block cost estimate (calibrated against server telemetry, NOTES.md).</summary>
    public static double CostScale { get; set; } = 0.65;

    public static FleetProgram Load(string name, string sourcePath)
    {
        string source = File.ReadAllText(sourcePath);
        return new FleetProgram(name, sourcePath, CompileType(source, sourcePath));
    }

    public static Type CompileType(string source, string label)
    {
        string hash = Hash(source + "\n//" + InstrumentationVersion + CostScale.ToString(System.Globalization.CultureInfo.InvariantCulture));
        lock (Gate)
        {
            if (Loaded.TryGetValue(hash, out var cached)) return cached;
            byte[] image;
            string? cacheFile = CacheDir == null ? null : Path.Combine(CacheDir, hash + ".dll");
            if (cacheFile != null && File.Exists(cacheFile)) image = File.ReadAllBytes(cacheFile);
            else
            {
                image = Compile(source, label, "Fleet_" + hash);
                if (cacheFile != null)
                {
                    Directory.CreateDirectory(CacheDir!);
                    File.WriteAllBytes(cacheFile, image);
                }
            }
            var asm = Assembly.Load(image);
            var types = asm.GetTypes().Where(t => t.IsPublic && !t.IsAbstract && typeof(Shtemer).IsAssignableFrom(t)).ToList();
            if (types.Count != 1)
                throw new InvalidOperationException($"{label}: expected exactly one public class deriving from Shtemer, found {types.Count}");
            if (types[0].GetConstructor(Type.EmptyTypes) == null)
                throw new InvalidOperationException($"{label}: the Shtemer class needs a parameterless constructor");
            Loaded[hash] = types[0];
            return types[0];
        }
    }

    private static byte[] Compile(string source, string label, string assemblyName)
    {
        var parse = new CSharpParseOptions(LanguageVersion.CSharp12);
        var tree = CSharpSyntaxTree.ParseText(source, parse, path: label, encoding: Encoding.UTF8);
        // The server accepts fleets that rely on these namespaces; a fleet that also declares them only gets a warning.
        var usings = CSharpSyntaxTree.ParseText(
            "global using System;\nglobal using System.Linq;\nglobal using System.Collections.Generic;\n", parse);
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable,
            allowUnsafe: false, concurrentBuild: true);
        // expression bodies need to know whether they return a value: mark them from the semantic model first
        var probe = CSharpCompilation.Create(assemblyName + "_probe", new[] { tree, usings }, References(), options);
        var marked = BudgetRewriter.MarkExpressionBodies(tree.GetRoot(), probe.GetSemanticModel(tree));
        var root = (CompilationUnitSyntax)new BudgetRewriter(CostScale).Visit(marked);
        tree = CSharpSyntaxTree.Create(root, parse, label, Encoding.UTF8);
        var comp = CSharpCompilation.Create(assemblyName, new[] { tree, usings }, References(), options);
        using var ms = new MemoryStream();
        var result = comp.Emit(ms);
        if (!result.Success)
        {
            var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(10).Select(d => d.ToString());
            throw new InvalidOperationException($"{label}: compilation failed\n" + string.Join("\n", errors));
        }
        return ms.ToArray();
    }

    private static ImmutableArray<MetadataReference> References()
    {
        if (!_refs.IsDefault) return _refs;
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.") || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
            .ToList();
        paths.Add(typeof(Shtemer).Assembly.Location);
        paths.Add(typeof(Runtime.Meter).Assembly.Location);
        _refs = paths.Distinct().Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToImmutableArray();
        return _refs;
    }

    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..24].ToLowerInvariant();
}

/// <summary>
/// Inserts <c>Meter.Step(n)</c> at the start of every block, n being a rough IL estimate of the block's own statements
/// (syntax nodes of those statements, not counting nested blocks). Loop bodies that are single statements are wrapped
/// in blocks first, so every loop iteration pays. Expression bodies (<c>=&gt; expr</c> methods, local functions,
/// properties, accessors, operators and lambdas) are turned into blocks that pay the same way (review H2: before, a
/// LINQ selector or a recursive <c>=&gt;</c> method ran unmetered). Lambdas converted to expression trees are left
/// alone. The estimate is coarse by design: it only has to stop runaway code and put a fleet in the right range of the
/// 50 000 budget.
/// </summary>
public sealed class BudgetRewriter : CSharpSyntaxRewriter
{
    private const string Mark = "shtemeri-meter";
    private const string Value = "value", Void = "void";
    private readonly double _scale;
    public BudgetRewriter(double scale) { _scale = scale; }

    /// <summary>Annotates every expression body with whether it returns a value ("value") or not ("void").
    /// Expression bodies without an annotation (expression trees, unresolved lambdas) are not rewritten.</summary>
    public static SyntaxNode MarkExpressionBodies(SyntaxNode root, SemanticModel model)
    {
        var marks = new Dictionary<SyntaxNode, string>();
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case LambdaExpressionSyntax { ExpressionBody: not null } lambda:
                {
                    var conv = model.GetTypeInfo(lambda).ConvertedType;
                    if (conv?.ContainingNamespace?.ToDisplayString() == "System.Linq.Expressions") break;
                    if (model.GetSymbolInfo(lambda).Symbol is IMethodSymbol m) marks[lambda] = Returns(m) ? Value : Void;
                    break;
                }
                case MethodDeclarationSyntax { ExpressionBody: not null } md:
                    if (model.GetDeclaredSymbol(md) is IMethodSymbol mm) marks[md] = Returns(mm) ? Value : Void;
                    break;
                case LocalFunctionStatementSyntax { ExpressionBody: not null } lf:
                    if (model.GetDeclaredSymbol(lf) is IMethodSymbol lm) marks[lf] = Returns(lm) ? Value : Void;
                    break;
            }
        }
        return root.ReplaceNodes(marks.Keys, (orig, cur) => cur.WithAdditionalAnnotations(new SyntaxAnnotation(Mark, marks[orig])));
    }

    private static bool Returns(IMethodSymbol m)
    {
        if (m.ReturnsVoid) return false;
        // async Task / ValueTask without a result: the body is a statement
        if (m.IsAsync && m.ReturnType is INamedTypeSymbol { IsGenericType: false }) return false;
        return true;
    }

    private static string? MarkOf(SyntaxNode n) => n.GetAnnotations(Mark).FirstOrDefault()?.Data;

    private BlockSyntax Metered(ExpressionSyntax original, ExpressionSyntax visited, bool returns)
    {
        StatementSyntax st = visited is ThrowExpressionSyntax te
            ? SyntaxFactory.ThrowStatement(te.Expression)
            : returns ? SyntaxFactory.ReturnStatement(visited.WithLeadingTrivia(SyntaxFactory.Space)) : SyntaxFactory.ExpressionStatement(visited);
        int cost = Math.Max(1, (int)Math.Round((Count(original) + (original is InvocationExpressionSyntax ? 3 : 1)) * _scale));
        var step = SyntaxFactory.ParseStatement($"global::Shtemeri.Sim.Runtime.Meter.Step({cost});");
        return SyntaxFactory.Block(step, st);
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var visited = (BlockSyntax)base.VisitBlock(node)!;
        int cost = Math.Max(1, (int)Math.Round(Cost(node) * _scale));
        var step = SyntaxFactory.ParseStatement($"global::Shtemeri.Sim.Runtime.Meter.Step({cost});");
        return visited.WithStatements(visited.Statements.Insert(0, step));
    }

    public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
    {
        var v = (SimpleLambdaExpressionSyntax)base.VisitSimpleLambdaExpression(node)!;
        var mark = MarkOf(node);
        if (node.ExpressionBody == null || mark == null) return v;
        return v.WithExpressionBody(null).WithBlock(Metered(node.ExpressionBody, v.ExpressionBody!, mark == Value));
    }

    public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
    {
        var v = (ParenthesizedLambdaExpressionSyntax)base.VisitParenthesizedLambdaExpression(node)!;
        var mark = MarkOf(node);
        if (node.ExpressionBody == null || mark == null) return v;
        return v.WithExpressionBody(null).WithBlock(Metered(node.ExpressionBody, v.ExpressionBody!, mark == Value));
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var v = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
        var mark = MarkOf(node);
        if (node.ExpressionBody == null || mark == null) return v;
        return v.WithExpressionBody(null).WithSemicolonToken(default)
                .WithBody(Metered(node.ExpressionBody.Expression, v.ExpressionBody!.Expression, mark == Value));
    }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        var v = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;
        var mark = MarkOf(node);
        if (node.ExpressionBody == null || mark == null) return v;
        return v.WithExpressionBody(null).WithSemicolonToken(default)
                .WithBody(Metered(node.ExpressionBody.Expression, v.ExpressionBody!.Expression, mark == Value));
    }

    public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax node)
    {
        var v = (OperatorDeclarationSyntax)base.VisitOperatorDeclaration(node)!;
        if (node.ExpressionBody == null) return v;
        return v.WithExpressionBody(null).WithSemicolonToken(default)
                .WithBody(Metered(node.ExpressionBody.Expression, v.ExpressionBody!.Expression, true));
    }

    public override SyntaxNode? VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node)
    {
        var v = (ConversionOperatorDeclarationSyntax)base.VisitConversionOperatorDeclaration(node)!;
        if (node.ExpressionBody == null) return v;
        return v.WithExpressionBody(null).WithSemicolonToken(default)
                .WithBody(Metered(node.ExpressionBody.Expression, v.ExpressionBody!.Expression, true));
    }

    public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        var v = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!;
        if (node.ExpressionBody == null) return v;
        return v.WithExpressionBody(null).WithSemicolonToken(default)
                .WithBody(Metered(node.ExpressionBody.Expression, v.ExpressionBody!.Expression, false));
    }

    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        var v = (PropertyDeclarationSyntax)base.VisitPropertyDeclaration(node)!;
        if (node.ExpressionBody == null) return v;
        var get = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration,
            Metered(node.ExpressionBody.Expression, v.ExpressionBody!.Expression, true));
        return v.WithExpressionBody(null).WithSemicolonToken(default)
                .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(get)));
    }

    public override SyntaxNode? VisitIndexerDeclaration(IndexerDeclarationSyntax node)
    {
        var v = (IndexerDeclarationSyntax)base.VisitIndexerDeclaration(node)!;
        if (node.ExpressionBody == null) return v;
        var get = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration,
            Metered(node.ExpressionBody.Expression, v.ExpressionBody!.Expression, true));
        return v.WithExpressionBody(null).WithSemicolonToken(default)
                .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(get)));
    }

    public override SyntaxNode? VisitAccessorDeclaration(AccessorDeclarationSyntax node)
    {
        var v = (AccessorDeclarationSyntax)base.VisitAccessorDeclaration(node)!;
        if (node.ExpressionBody == null) return v;
        bool returns = node.IsKind(SyntaxKind.GetAccessorDeclaration);
        return v.WithExpressionBody(null).WithSemicolonToken(default)
                .WithBody(Metered(node.ExpressionBody.Expression, v.ExpressionBody!.Expression, returns));
    }

    public override SyntaxNode? VisitForStatement(ForStatementSyntax node) =>
        base.VisitForStatement(node.WithStatement(AsBlock(node.Statement)));
    public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node) =>
        base.VisitForEachStatement(node.WithStatement(AsBlock(node.Statement)));
    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node) =>
        base.VisitWhileStatement(node.WithStatement(AsBlock(node.Statement)));
    public override SyntaxNode? VisitDoStatement(DoStatementSyntax node) =>
        base.VisitDoStatement(node.WithStatement(AsBlock(node.Statement)));

    private static StatementSyntax AsBlock(StatementSyntax s) => s is BlockSyntax ? s : SyntaxFactory.Block(s);

    /// <summary>Syntax nodes of the block's own statements, skipping nested blocks and lambdas (they meter themselves).</summary>
    private static int Cost(BlockSyntax block)
    {
        int n = 0;
        foreach (var st in block.Statements) n += Count(st);
        return n;
    }

    private static int Count(SyntaxNode node)
    {
        int n = 0;
        foreach (var child in node.ChildNodes())
        {
            if (child is BlockSyntax || child is AnonymousFunctionExpressionSyntax || child is LocalFunctionStatementSyntax) continue;
            if (child is StatementSyntax && (node is IfStatementSyntax || node is ElseClauseSyntax))
            {
                // a single-statement if/else branch runs only sometimes; count it at half weight
                n += Count(child) / 2;
                continue;
            }
            n += child is InvocationExpressionSyntax ? 3 : 1;
            n += Count(child);
        }
        return n;
    }
}
