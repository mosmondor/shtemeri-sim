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
    private const string InstrumentationVersion = "budget-v1";
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
        var root = (CompilationUnitSyntax)new BudgetRewriter(CostScale).Visit(tree.GetRoot());
        tree = CSharpSyntaxTree.Create(root, parse, label, Encoding.UTF8);
        // The server accepts fleets that rely on these namespaces; a fleet that also declares them only gets a warning.
        var usings = CSharpSyntaxTree.ParseText(
            "global using System;\nglobal using System.Linq;\nglobal using System.Collections.Generic;\n", parse);
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable,
            allowUnsafe: false, concurrentBuild: true);
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
/// in blocks first, so every loop iteration pays. Expression-bodied members and expression lambdas are not metered
/// (an undercount; such code is usually short). The estimate is coarse by design: it only has to stop runaway code and
/// put a fleet in the right range of the 50 000 budget.
/// </summary>
public sealed class BudgetRewriter : CSharpSyntaxRewriter
{
    private readonly double _scale;
    public BudgetRewriter(double scale) { _scale = scale; }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var visited = (BlockSyntax)base.VisitBlock(node)!;
        int cost = Math.Max(1, (int)Math.Round(Cost(node) * _scale));
        var step = SyntaxFactory.ParseStatement($"global::Shtemeri.Sim.Runtime.Meter.Step({cost});");
        return visited.WithStatements(visited.Statements.Insert(0, step));
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
