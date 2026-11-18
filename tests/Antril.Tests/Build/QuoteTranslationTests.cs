// `[CompileWith]`: quoted functions translated by a library's function while `larva build` builds the program; what it
// gives is the Quoted's Compiled, a `!` is an error of the build at the function written.
namespace Antril.Tests;

public sealed class QuoteTranslationTests : CheckedTest
{
    private const string Library = """
        import System;

        public class Box { public: i32 Size; new(i32 Size); }

        public static class Tr
        {
            public:
                /// What the build runs: the tree as text, or an error when it names `Secret`.
                static Go(QuoteNode tree) : str
                {
                    str text = tree.ToString();
                    return text.Contains("Secret") ? "!" + text + " names Secret" : "T:" + text;
                }

                /// Generic: the type argument of the method's type.
                static Typed<T>(QuoteNode tree) : str => "G:" + nameof(T) + ":" + tree.ToString();
        }

        public class Holder<T>
        {
            public:
                new() { }
                Take([CompileWith("Tr.Typed")] const func<T> : bool f) : str => f.Compiled ?? "none";
        }

        Take([CompileWith("Tr.Go")] const func<Box> : bool f) : str => f.Compiled ?? "none";

        Plain(const func<Box> : bool f) : str => f.Compiled ?? "none";
        """;

    [Fact]
    public void Quoted_functions_are_translated_when_the_program_is_built()
    {
        string root = TestEnvironment.TempFolder("compile-with");
        File.WriteAllText(Path.Combine(root, "Lib.ant"), Library);
        File.WriteAllText(Path.Combine(root, "Main.ant"), """
            import System;

            Main() : i32
            {
                i32 limit = 3;
                Console.WriteLine(Take(b => b.Size > limit));
                Console.WriteLine(new Holder<Box>().Take(b => b.Size == 1));
                Console.WriteLine(Plain(b => b.Size > 0));
                return 0;
            }
            """);
        string exe = Path.Combine(root, "bin", "ok.exe");
        var (code, text) = LarvaCommand.Run("build", "--backend", "x64", "-o", exe, Path.Combine(root, "Lib.ant"), Path.Combine(root, "Main.ant"));
        Check("a program with quoted functions to translate builds", code == 0, text.Trim());
        if (code == 0)
        {
            string printed = LarvaCommand.Execute(exe).Replace("\r\n", "\n");
            Check("each gets what its translator gave (generic over the method's type: Box); without [CompileWith], nothing",
                printed.StartsWith("T:(b.Size > limit)\nG:Box:(b.Size == 1)\nnone\n", StringComparison.Ordinal), printed);
        }

        File.WriteAllText(Path.Combine(root, "Main.ant"), """
            import System;

            Main() : i32
            {
                Console.WriteLine(Take(b => b.Size > 1));
                Console.WriteLine(Take(b => b.Secret()));
                return 0;
            }
            """);
        File.WriteAllText(Path.Combine(root, "Lib.ant"), Library.Replace("public class Box { public: i32 Size; new(i32 Size); }",
            "public class Box { public: i32 Size; new(i32 Size); Secret() : bool => true; }"));
        var (badCode, badText) = LarvaCommand.Run("build", "--backend", "x64", "-o", Path.Combine(root, "bin", "bad.exe"), Path.Combine(root, "Lib.ant"), Path.Combine(root, "Main.ant"));
        Check("a translator's `!` is an error of the build, at the function written (line 6, column 28)",
            badCode != 0 && badText.Contains("Main.ant(6,28): error: b.Secret() names Secret"), badText.Trim());
    }
}
