// Quoted functions (`const func` parameters): the lambda, its tree made once per place, its captures worked out at each
// call, on the VM.
using Antril.Binding;
using Antril.Syntax;
using Larva.Hir.Encoding;
using Larva.Hir.Linking;
using Larva.Hir.Lowering;
using Larva.Runtime.Vm;

namespace Antril.Tests;

public sealed class QuotingTests : CheckedTest
{
    private const string Program = """
        import System;

        class User { public: str Name; i32 Age; str? Nick; new(str Name, i32 Age); }

        Describe<T>(const func<T> : bool test, T sample) : str
        {
            func<T> : bool run = test.Function;
            return test.Body.ToString() + "|" + $"{test.Captures.Length}" + "|" + $"{run(sample)}";
        }

        Key<T, K>(const func<T> : K field) : str => field.Body.ToString();

        Site<T>(const func<T> : bool test) : i64 => test.Site;

        Main() : i32
        {
            i32 limit = 18;
            str prefix = "A";
            User ana = new User("Ana", 30);
            i32 r = 0;
            str first = Describe(u => u.Age > limit && u.Name.StartsWith(prefix) || u.Nick == null, ana);
            r += first == "(((u.Age > limit) && u.Name.StartsWith(prefix)) || (u.Nick == null))|2|true" ? 1 : 0;
            // A sub-expression that does not name the parameter is one capture, worked out here.
            r += Describe((User u) => !(u.Age < limit * 2), ana) == "!(u.Age < limit * 2)|1|false" ? 10 : 0;
            r += Key((User u) => u.Name) == "u.Name" ? 100 : 0;
            // One place: the same number at every call; another place: another.
            i64 a = 0;
            i64 b = 0;
            loop (i32 i = 0..3)
            {
                i64 here = Site((User u) => u.Age > i);
                if (a == 0)
                    a = here;
                r += here == a ? 0 : 1000000;
                b = Site((User u) => u.Age > i);
            }
            r += a != b ? 1000 : 0;
            // The captures are this call's values.
            limit = 40;
            r += Describe((User u) => u.Age > limit, ana) == "(u.Age > limit)|1|false" ? 10000 : 0;
            return r;
        }
        """;

    [Fact]
    public void Quoted_functions_carry_their_tree_captures_and_place()
    {
        var core = AntrilCompiler.Compile(Repositories.LibrarySources("System.Core"), "System.Core");
        Check("System.Core compiles", core.Success, string.Join("; ", core.Diagnostics.Take(4)));
        if (!core.Success)
            return;
        byte[] coreBytes = LvaFormat.Write(core.Assembly!);
        var program = AntrilCompiler.Compile([new SourceText(Program, "quoted.ant")], [LvaFormat.Read(coreBytes)], "Quoted");
        Check("the program compiles", program.Success, string.Join("\n    ", program.Diagnostics.Take(8)));
        if (!program.Success)
            return;
        var linked = HirLinker.Link("Quoted", [coreBytes, LvaFormat.Write(program.Assembly!)]);
        var lowered = HirLowerer.Lower(linked.Assembly!, new LoweringOptions { StaticConstructorsFromEntry = true });
        var vm = lowered.Success ? Larva.IR.Compiler.Compile(lowered.Module!) : null;
        Check("it lowers and passes the IR checks", vm is { Success: true }, string.Join("; ", lowered.Errors.Take(4)) + string.Join("; ", vm?.TypeErrors.Take(4) ?? []));
        if (vm is not { Success: true })
            return;
        object? result = new Interpreter(vm.Il!).Run(lowered.FunctionName(linked.Assembly!.EntryPoint!.Value));
        Check("Main() = 11111: trees, captures, callable, one site per place, captures per call", Equals(result, 11111L) || Equals(result, 11111), $"{result}");
    }

    [Fact]
    public void A_quoted_function_is_one_expression()
    {
        var core = AntrilCompiler.Compile(Repositories.LibrarySources("System.Core"), "System.Core");
        var reference = LvaFormat.Read(LvaFormat.Write(core.Assembly!));
        var result = AntrilCompiler.Compile([new SourceText("Take(const func<i32> : bool f) : i32 => 0; F() : i32 => Take(x => { return x > 1; });", "block.ant")], [reference], "Block");
        Check("a block body: AN2148", result.Diagnostics.Any(d => d.Code == "AN2148"), string.Join("; ", result.Diagnostics.Take(4)));
    }
}
