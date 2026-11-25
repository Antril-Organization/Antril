// System.Data with SQLite: a program that creates, writes and reads a database, built on both native back ends.
namespace Antril.Tests;

/// <summary>System.Data (linked when a source names it) through sqlite3.dll: the system's SQLite copied in its place.</summary>
public sealed class DataTests : CheckedTest
{
    private const string Program = """
        import System;
        import System.Data;
        import System.Data.Sqlite;

        Main() : i32
        {
            SqliteConnection db = SqliteConnection.InMemory();
            db.Execute("CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, price REAL, data BLOB)");
            Command insert = db.Prepare("INSERT INTO items (name, price, data) VALUES (?1, ?2, ?3)");
            db.InTransaction(() =>
            {
                loop (i32 i = 0..1000)
                {
                    insert.Bind(1, $"item{i}");
                    insert.Bind(2, i * 0.25);
                    if (i % 2 == 0)
                        insert.Bind(3, [(u8)i, (u8)(i >> 8), 7]);
                    else
                        insert.BindNull(3);
                    insert.Run();
                }
            });
            insert.Close();
            Console.WriteLine($"count {db.QueryI64("SELECT count(*) FROM items", -1)} last {db.LastInsertId()}");

            Command query = db.Prepare("SELECT id, name, price, data FROM items WHERE id >= :from ORDER BY id LIMIT 2");
            query.Bind(query.ParameterIndex(":from"), 10i64);
            loop (query.Step())
                Console.WriteLine($"row {query.GetI64(0)} {query.GetText(1)} {query.GetF64(2)} {(query.IsNull(3) ? "null" : $"{query.GetBytes(3).Length}")} {query.ColumnName(2)}");
            query.Close();

            try
            {
                db.Execute("INSERT INTO items (name) VALUES ('item1')");
            }
            catch (DataError e)
            {
                Console.WriteLine($"refused {e.Code}");
            }
            try
            {
                db.InTransaction(() =>
                {
                    db.Execute("DELETE FROM items");
                    throw new InvalidOperationError("undo");
                });
            }
            catch (InvalidOperationError e)
            {
                Console.WriteLine($"kept {db.QueryI64("SELECT count(*) FROM items", -1)}");
            }
            db.Close();
            Console.WriteLine($"open {db.IsOpen()}");
            return 0;
        }
        """;

    [Fact]
    public void A_program_uses_SQLite_through_System_Data()
    {
        string sqlite = Path.Combine(Environment.SystemDirectory, "winsqlite3.dll");
        Assert.SkipUnless(OperatingSystem.IsWindows() && File.Exists(sqlite), "the test uses the system's SQLite (winsqlite3.dll)");
        string folder = TestEnvironment.TempFolder("data");
        Directory.CreateDirectory(Path.Combine(folder, "native"));
        File.Copy(sqlite, Path.Combine(folder, "native", "sqlite3.dll"), overwrite: true);
        File.WriteAllText(Path.Combine(folder, "Main.ant"), Program);
        File.WriteAllText(Path.Combine(folder, "project.queen"), "queen 1.0;\n\nchunk PROJ:\n   name: Data\n\nchunk DEPS:\n   lib: native/sqlite3.dll\n");
        foreach (string backend in new[] { "llvm", "x64" })
        {
            string output = Path.Combine(folder, "bin", backend, "Data.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var (code, text) = LarvaCommand.Run(new Dictionary<string, string>(), folder, "build", "--backend", backend, "-o", output, Path.Combine(folder, "project.queen"));
            Check($"{backend}: the program builds, sqlite3.dll next to it", code == 0 && File.Exists(Path.Combine(folder, "bin", backend, "sqlite3.dll")), text.Trim());
            if (code != 0)
                continue;
            string printed = LarvaCommand.Execute(output).Replace("\r\n", "\n");
            Check($"{backend}: writes, reads, NULL and blobs, errors with their codes, rollback",
                printed.Contains("count 1000 last 1000\nrow 10 item9 2.25 null price\nrow 11 item10 2.5 3 price\nrefused 2067\nkept 1000\nopen false"), printed);
        }
    }
}
