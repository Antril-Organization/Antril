// Nest (the repository next to this one): a project's entities and database on SQLite, and its migrations made by the
// Nest tool from the model the code declares, applied, changed, applied again with the rows kept, and reverted.
using System.Diagnostics;

namespace Antril.Tests;

/// <summary>Nest end to end: Nest.dll, the tool built as `larva nest` builds it, a project migrated twice and reverted.</summary>
public sealed class NestTests : CheckedTest
{
    private static string NestRepository => Path.GetFullPath(Path.Combine(Repositories.Antril, "..", "Nest"));

    /// <summary>A program run with more environment: its exit code and everything it wrote.</summary>
    private static (int Code, string Output) Execute(string exe, string folder, Dictionary<string, string> environment, params string[] arguments)
    {
        var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = folder };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        foreach (var (name, value) in environment)
            start.Environment[name] = value;
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(180_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            return (-1, "(stopped)\n" + output.Result + error.Result);
        }
        process.WaitForExit();
        return (process.ExitCode, output.Result + error.Result);
    }

    private const string Data = """
        import System;
        import System.Data.Sqlite;
        import Nest;

        into namespace ShopApp
        {
            public class User : IEntity<User>
            {
                public:
                    [Key] i64 Id;
                    [Unique] str Email;
                    str Name;
                    i32 Age;

                    new() { Email = ""; Name = ""; }
                    new(str Email, str Name, i32 Age) { this.Email = Email; this.Name = Name; this.Age = Age; }
            }

            public class ShopDb : Database, IModel<ShopDb>
            {
                public:
                    Table<User> Users;

                    new(str path) : base(SqliteConnection.Open(path)) { OpenTables(Connection); }
            }
        }
        """;

    private const string Main = """
        import System;
        import Nest;
        import ShopApp;

        Main() : i32
        {
            ShopDb db = new ShopDb("shop.db");
            if (Environment.GetEnvironmentVariable("SHOP_REVERT") is str target)
            {
                Console.WriteLine($"reverted {db.Revert(Migrations.All(), Int64.Parse(target)).Applied}");
                db.Close();
                return 0;
            }
            Console.WriteLine($"applied {db.Migrate(Migrations.All()).Applied}");
            db.Users.Add(new User($"u{db.Users.Count()}@x.org", "Ana", 30));
            loop (User u in db.Users.All())
                Console.WriteLine($"row {u.Id} {u.Name}");
            db.Close();
            return 0;
        }
        """;

    [Fact]
    public void Queries_from_functions_run_as_prepared_SQL()
    {
        string sqlite = Path.Combine(Environment.SystemDirectory, "winsqlite3.dll");
        Assert.SkipUnless(OperatingSystem.IsWindows() && File.Exists(sqlite) && Directory.Exists(Path.Combine(NestRepository, "src")),
            "needs the Nest repository next to this one and the system's SQLite (winsqlite3.dll)");
        string root = TestEnvironment.TempFolder("nest-query");
        string nestDll = Path.Combine(root, "Nest.dll");
        var (libCode, libText) = LarvaCommand.Run(["lib", "-o", nestDll, .. Directory.GetFiles(Path.Combine(NestRepository, "src"), "*.ant")]);
        Check("Nest.dll compiles", libCode == 0, libText.Trim());
        if (libCode != 0)
            return;
        Directory.CreateDirectory(Path.Combine(root, "native"));
        File.Copy(sqlite, Path.Combine(root, "native", "sqlite3.dll"));
        string queen = Path.Combine(root, "project.queen");
        File.WriteAllText(queen, $"queen 1.0;\n\nchunk PROJ:\n   name: Query\n\nchunk DEPS:\n   assembly: \"{nestDll.Replace('\\', '/')}\",\n   lib: native/sqlite3.dll\n");
        File.WriteAllText(Path.Combine(root, "Main.ant"), """
            import System;
            import System.Data.Sqlite;
            import Nest;

            class User : IEntity<User>
            {
                public:
                    [Key] i64 Id;
                    str Email;
                    [Column("full_name")] str Name;
                    i32 Age;
                    str? Nick;

                    new() { Email = ""; Name = ""; }
                    new(str Email, str Name, i32 Age) { this.Email = Email; this.Name = Name; this.Age = Age; }
            }

            class Shop : Database, IModel<Shop>
            {
                public:
                    Table<User> Users;
                    new() : base(SqliteConnection.InMemory()) { OpenTables(Connection); }
            }

            Adults(Shop db, i32 limit) : i64 => db.Users.Where(u => u.Age >= limit).Count();

            Main() : i32
            {
                Shop db = new Shop();
                db.Users.Create();
                List<User> many = new List<User>();
                loop (i32 i = 0..900)
                {
                    User u = new User($"u{i}@x.org", i % 3 == 0 ? $"Ana {i}" : $"Bo {i}", i % 90);
                    if (i % 7 == 0)
                        u.Nick = $"n{i}";
                    many.Add(u);
                }
                db.Users.AddAll(many);
                str prefix = "Ana";
                i32 limit = 30;
                loop (User u in db.Users.Where(u => u.Age >= limit && u.Name.StartsWith(prefix)).OrderByDescending(u => u.Age).Take(2))
                    Console.WriteLine($"top {u.Id} {u.Name} {u.Age}");
                Console.WriteLine($"adults {Adults(db, 18)} {Adults(db, 80)}");
                Console.WriteLine($"nicks {db.Users.Where(u => u.Nick != null).Count()} {db.Users.Where(u => u.Nick == null && u.Age < 2).Count()}");
                Console.WriteLine($"text {db.Users.Where(u => u.Email.Contains("99@")).Count()} {db.Users.Where(u => u.Email.EndsWith("0@x.org")).Count()}");
                Console.WriteLine($"page {db.Users.Query().OrderBy(u => u.Id).Skip(10).Take(2).ToList()[0].Id}");
                Console.WriteLine($"deleted {db.Users.Where(u => u.Age > 85).Delete()} left {db.Users.Count()}");
                return 0;
            }
            """);
        foreach (string backend in new[] { "llvm", "x64" })
        {
            string output = Path.Combine(root, "bin", backend, "Query.exe");
            var (code, text) = LarvaCommand.Run(new Dictionary<string, string>(), root, "build", "--backend", backend, "-o", output, queen);
            Check($"{backend}: it builds", code == 0, text.Trim());
            if (code != 0)
                continue;
            string printed = LarvaCommand.Execute(output).Replace("\r\n", "\n");
            Check($"{backend}: filters with captures, order and paging, counts, NULL, text calls, a set delete",
                printed.Contains("top 88 Ana 87 87\ntop 178 Ana 177 87\nadults 720 100\nnicks 129 ") && printed.Contains("text 9 90\npage 11\ndeleted 40 left 860"), printed);
        }
        // What SQL cannot say is an error of the build, where the function is written.
        string main = Path.Combine(root, "Main.ant");
        File.WriteAllText(main, File.ReadAllText(main).Replace("return 0;", "i64 bad = db.Users.Where(u => u.Name.Replace(\"a\", \"b\") == \"x\").Count();\n    return 0;"));
        var (badCode, badText) = LarvaCommand.Run(new Dictionary<string, string>(), root, "build", "--backend", "x64", "-o", Path.Combine(root, "bin", "bad.exe"), queen);
        Check("a query SQL cannot say does not build: the error names what, where",
            badCode != 0 && badText.Contains("Main.ant(") && badText.Contains("`u.Name.Replace(\"a\", \"b\")`: `Replace` cannot be said in SQL"), badText.Trim());
    }

    [Fact]
    public void Migrations_are_made_from_the_model_applied_and_reverted()
    {
        string sqlite = Path.Combine(Environment.SystemDirectory, "winsqlite3.dll");
        Assert.SkipUnless(OperatingSystem.IsWindows() && File.Exists(sqlite) && Directory.Exists(Path.Combine(NestRepository, "src")),
            "needs the Nest repository next to this one and the system's SQLite (winsqlite3.dll)");
        string root = TestEnvironment.TempFolder("nest");
        string larvaExe = Path.ChangeExtension(Repositories.LarvaCli, ".exe");

        // Nest and its tool, built as a package and `larva tool install` would build them.
        string nestDll = Path.Combine(root, "Nest.dll");
        var (libCode, libText) = LarvaCommand.Run(["lib", "-o", nestDll, .. Directory.GetFiles(Path.Combine(NestRepository, "src"), "*.ant")]);
        Check("Nest.dll compiles", libCode == 0, libText.Trim());
        string tool = Path.Combine(root, "tool", "nest.exe");
        var (toolCode, toolText) = LarvaCommand.Run("build", "--backend", "x64", "-o", tool, Path.Combine(NestRepository, "tool", "project.queen"));
        Check("the tool builds", toolCode == 0, toolText.Trim());
        if (libCode != 0 || toolCode != 0)
            return;

        string shop = Path.Combine(root, "shop");
        Directory.CreateDirectory(Path.Combine(shop, "native"));
        Directory.CreateDirectory(Path.Combine(shop, "src"));
        File.Copy(sqlite, Path.Combine(shop, "native", "sqlite3.dll"));
        string queen = Path.Combine(shop, "project.queen");
        File.WriteAllText(queen, $"queen 1.0;\n\nchunk PROJ:\n   name: Shop\n\nchunk DEPS:\n   assembly: \"{nestDll.Replace('\\', '/')}\",\n   lib: native/sqlite3.dll\n");
        File.WriteAllText(Path.Combine(shop, "src", "Data.ant"), Data);
        File.WriteAllText(Path.Combine(shop, "src", "Main.ant"), Main);
        var environment = new Dictionary<string, string> { ["LARVA_EXE"] = larvaExe, ["LARVA_PROJECT"] = queen };
        (int, string) Nest(params string[] arguments) => Execute(tool, shop, environment, arguments);
        string program = Path.Combine(shop, "bin", "Shop.exe");
        (int, string) Build() => LarvaCommand.Run(new Dictionary<string, string>(), shop, "build", "--backend", "x64", queen);
        (int, string) RunShop(Dictionary<string, string>? more = null) => Execute(program, Path.Combine(shop, "bin"), more ?? []);

        var (modelCode, model) = Nest("model");
        Check("larva nest model: the model the code declares", modelCode == 0 && model.Contains("table|User") && model.Contains("column|Email|TEXT|notnull,unique|"), model.Trim());
        var (checkCode, checkText) = Nest("migrate", "check");
        Check("migrate check before any migration: changes (exit 1)", checkCode == 1 && checkText.Contains("table User added"), checkText.Trim());
        var (addCode, addText) = Nest("migrate", "add", "Init");
        string first = Path.Combine(shop, "migrations", "M0001_Init.ant");
        Check("migrate add Init: the migration, the index and the model's snapshot",
            addCode == 0 && File.Exists(first) && File.ReadAllText(first).Contains("CREATE TABLE") && File.Exists(Path.Combine(shop, "migrations", "model.txt")), addText.Trim());

        var (buildCode, buildText) = Build();
        Check("the project builds with its migrations", buildCode == 0, buildText.Trim());
        var (_, firstRun) = RunShop();
        Check("its first run applies the migration and writes a row", firstRun.Contains("applied 1") && firstRun.Contains("row 1 Ana"), firstRun.Trim());

        // The model changes: a column renamed (its values kept) and one added.
        File.WriteAllText(Path.Combine(shop, "src", "Data.ant"), Data
            .Replace("str Name;", "[Renamed(\"Name\")] str FullName;\n                    str? Phone;")
            .Replace("Name = \"\";", "FullName = \"\";").Replace("this.Name = Name;", "this.FullName = Name;"));
        File.WriteAllText(Path.Combine(shop, "src", "Main.ant"), Main.Replace("{u.Name}", "{u.FullName}"));
        var (scriptCode, script) = Nest("migrate", "script");
        Check("migrate script: the rename and the new column", scriptCode == 0 && script.Contains("RENAME COLUMN \"Name\" TO \"FullName\"") && script.Contains("ADD COLUMN \"Phone\" TEXT"), script.Trim());
        var (secondCode, secondText) = Nest("migrate", "add", "Contact");
        Check("migrate add Contact", secondCode == 0 && File.Exists(Path.Combine(shop, "migrations", "M0002_Contact.ant")), secondText.Trim());
        var (rebuildCode, rebuildText) = Build();
        Check("the project builds again", rebuildCode == 0, rebuildText.Trim());
        var (_, secondRun) = RunShop();
        Check("the next run applies the second migration; the first row is kept (renamed column)", secondRun.Contains("applied 1") && secondRun.Contains("row 1 Ana") && secondRun.Contains("row 2 Ana"), secondRun.Trim());
        var (_, upToDate) = Nest("migrate", "check");
        Check("then the model has no changes", upToDate.Contains("no changes"), upToDate.Trim());

        var (_, reverted) = RunShop(new() { ["SHOP_REVERT"] = "1" });
        Check("Revert to 1 undoes the second", reverted.Contains("reverted 1"), reverted.Trim());
        var (_, again) = RunShop();
        Check("and Migrate applies it again, the rows kept", again.Contains("applied 1") && again.Contains("row 3 Ana") && again.Contains("row 1 Ana"), again.Trim());
    }
}
